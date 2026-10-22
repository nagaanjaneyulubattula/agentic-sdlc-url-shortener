using System.Text.Json.Nodes;

namespace Agentic.Orchestrator;

public sealed class EngineOptions
{
    public required string RunsRoot { get; set; }
    public required ITestRunner TestRunner { get; set; }
    public required IApprovalProvider Approvals { get; set; }
    public string? RunId { get; set; }
    /// <summary>Max nodes executing at once (parallel paths).</summary>
    public int Concurrency { get; set; } = 6;
    /// <summary>Global retry budget for the whole run – exhausting it forces fallback/safe-stop (runaway protection).</summary>
    public int MaxTotalRetries { get; set; } = 25;
    public int MaxClarificationRounds { get; set; } = 2;
    public TimeSpan NodeTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeProvider Time { get; set; } = TimeProvider.System;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);
    /// <summary>Override the initial plan (used by unit tests with fake agents). Default: <see cref="PlanBuilder.Initial"/>.</summary>
    public Func<ScenarioSpec, IEnumerable<NodeSpec>>? Plan { get; set; }
}

public sealed record RunResult(string RunId, RunStatus Status, string? StopReason, string? ReleaseDecision, string RunDirectory, string WorkspaceRoot);

/// <summary>
/// The orchestration engine: a stateful scheduler over an explicit dependency graph.
/// • entry/exit gates per node, parallel execution of independent paths with synchronisation barriers (deps)
/// • bounded retries with exponential backoff → fallback agent → journal rollback → safe-stop (run halts, state preserved, resumable)
/// • human approval checkpoints (before/after) + clarification loops; change-control and policy guardrails enforced engine-wide
/// • decision lineage, hash-chained audit log, reliability metrics
/// • dynamic planning: architect expands the graph; content-addressed input ledger re-plans only what a changed upstream artifact invalidates
/// </summary>
public sealed class Engine
{
    private readonly FeatureCatalog _catalog;
    private readonly AgentRegistry _agents;
    private readonly EngineOptions _o;
    private readonly IReadOnlyDictionary<string, IGate> _gates = Gates.CreateDefault();
    private readonly WorkspaceManager _ws;
    private CancellationTokenSource? _kill;
    private bool _initialized;
    private int _approvalSeq;

    public RunState State { get; }
    public AuditLog Audit { get; }
    public string WorkspaceRoot => _ws.Root;
    public MetricsView Metrics() => MetricsCalculator.Compute(State, BuildGraph(), Now);
    public DependencyGraph BuildGraph() { lock (State.Sync) return new DependencyGraph(State.Nodes.Values.Where(n => n.Status != NodeStatus.Cancelled).Select(n => n.Spec).ToList()); }

    private DateTimeOffset Now => _o.Time.GetUtcNow();

    public Engine(ScenarioSpec scenario, FeatureCatalog catalog, IEnumerable<IAgent> agents, EngineOptions options)
    {
        _catalog = catalog;
        _o = options;
        _agents = new AgentRegistry(agents, scenario.Faults);
        var runId = options.RunId ?? $"{scenario.Id}-{options.Time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var runDir = Path.Combine(options.RunsRoot, runId);
        Directory.CreateDirectory(runDir);
        State = new RunState { RunId = runId, Scenario = scenario, RunDirectory = runDir };
        Audit = new AuditLog(options.Time, Path.Combine(runDir, "audit.jsonl"));
        _ws = new WorkspaceManager(Path.Combine(runDir, "workspace"));
    }

    // ───────────────────────────── public control surface ─────────────────────────────

    public async Task<RunResult> RunAsync(CancellationToken ct = default)
    {
        Initialize();
        return await LoopAsync(ct);
    }

    /// <summary>Safe-stop recovery: clears the halt, re-queues failed/blocked/rejected nodes (done work is kept) and continues.</summary>
    public async Task<RunResult> ResumeAsync(CancellationToken ct = default)
    {
        lock (State.Sync)
        {
            State.Halted = false; State.RejectedByHuman = false; State.StopReason = null; State.EndedAt = null; State.TotalRetries = 0;
            foreach (var n in State.Nodes.Values.Where(n => n.Status is NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Rejected or NodeStatus.Running or NodeStatus.AwaitingApproval))
            { n.Status = NodeStatus.Pending; n.Error = null; }
            State.Status = RunStatus.Running;
        }
        A("run.resumed", null, "engine", "Run resumed after safe-stop; completed work is preserved");
        return await LoopAsync(ct);
    }

    /// <summary>Kill switch: aborts in-flight work (rolled back) and safe-stops the run.</summary>
    public void RequestStop(string reason)
    {
        lock (State.Sync) { State.Halted = true; State.StopReason ??= reason; }
        A("run.stop-requested", null, "human:operator", reason);
        try { _kill?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ───────────────────────────── scheduler ─────────────────────────────

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        State.StartedAt = Now;
        A("run.started", null, "engine", $"Run {State.RunId} started: scenario '{State.Scenario.Id}' ({State.Scenario.Mode}, workspace={State.Scenario.Workspace})",
            new { requirement = State.Scenario.RequirementText, injectedFaults = State.Scenario.Faults.Count, scheduledEvents = State.Scenario.Events.Count });
        var seeded = WorkspaceSeeder.Seed(_catalog, State.Scenario, _ws.Root);
        if (seeded > 0) A("workspace.seeded", null, "engine", $"Seeded existing v1 codebase ({seeded} files) – brownfield starting point", new { knownDefectsNotTold = _catalog.Baseline.SeededDefects.Count });
        foreach (var spec in (_o.Plan ?? PlanBuilder.Initial)(State.Scenario)) State.Nodes[spec.Id] = new NodeState { Spec = spec };
        var g = BuildGraph();
        g.Validate();
        A("graph.validated", null, "engine", $"Dependency graph valid: {g.Nodes.Count} nodes in {g.Layers().Count} layers (acyclic, all deps resolved)", new { layers = g.Layers() });
    }

    private async Task<RunResult> LoopAsync(CancellationToken ct)
    {
        using var kill = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _kill = kill;
        lock (State.Sync) State.Status = RunStatus.Running;
        var running = new Dictionary<string, Task>();
        while (true)
        {
            List<string> start;
            lock (State.Sync)
            {
                ResolveSkipsAndBlocks();
                start = State.Halted || kill.IsCancellationRequested ? new() : TakeReady(_o.Concurrency - running.Count);
            }
            foreach (var id in start) { var cap = id; running[cap] = Task.Run(() => ExecuteNodeSafe(cap, kill.Token)); }
            if (running.Count == 0) break;
            await Task.WhenAny(running.Values);
            foreach (var k in running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList()) running.Remove(k);
            Persist();
        }
        return Finish();
    }

    private List<NodeState> LiveDeps(NodeState n) =>
        n.Spec.Deps.Where(State.Nodes.ContainsKey).Select(d => State.Nodes[d]).Where(d => d.Status != NodeStatus.Cancelled).ToList();

    private bool ConditionHolds(NodeSpec s) => s.Condition switch
    {
        null => true,
        "workspace:empty" => State.Scenario.Workspace == "empty",
        "workspace:baseline" => State.Scenario.Workspace == "baseline",
        _ => true,
    };

    private void ResolveSkipsAndBlocks()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var n in State.Nodes.Values.Where(n => n.Status is NodeStatus.Pending or NodeStatus.Stale).ToList())
            {
                var deps = LiveDeps(n);
                var bad = deps.FirstOrDefault(d => d.Status is NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Rejected);
                if (bad is not null)
                {
                    n.Status = NodeStatus.Blocked; n.Error = $"blocked: dependency '{bad.Spec.Id}' is {bad.Status}";
                    A("node.blocked", n.Spec.Id, "engine", n.Error); changed = true; continue;
                }
                if (deps.All(d => d.Status is NodeStatus.Done or NodeStatus.Skipped) && !ConditionHolds(n.Spec))
                {
                    n.Status = NodeStatus.Skipped;
                    A("node.skipped", n.Spec.Id, "engine", $"condition '{n.Spec.Condition}' is false for this scenario"); changed = true;
                }
            }
        } while (changed);
    }

    private List<string> TakeReady(int slots)
    {
        var list = new List<string>();
        foreach (var n in State.Nodes.Values)
        {
            if (list.Count >= slots) break;
            if (n.Status is not (NodeStatus.Pending or NodeStatus.Stale)) continue;
            if (!LiveDeps(n).All(d => d.Status is NodeStatus.Done or NodeStatus.Skipped)) continue;
            n.Status = NodeStatus.Running; n.StartedAt = Now; n.EndedAt = null; n.Generation++; n.Superseded = false; n.Error = null; n.ApprovalWaitMs = 0;
            n.InputHashes = n.Spec.Reads.ToDictionary(r => r, r => State.Artifacts.LatestHash(r) ?? "∅");
            list.Add(n.Spec.Id);
        }
        return list;
    }

    private RunResult Finish()
    {
        lock (State.Sync)
        {
            foreach (var n in State.Nodes.Values.Where(n => n.Status is NodeStatus.Pending or NodeStatus.Stale or NodeStatus.Running or NodeStatus.AwaitingApproval))
            { n.Status = NodeStatus.Blocked; n.Error ??= State.Halted ? "not executed: run was safe-stopped" : "not executed: upstream failure"; }
            State.EndedAt = Now;
            State.Status = State.Halted ? (State.RejectedByHuman ? RunStatus.Rejected : RunStatus.SafeStopped)
                : State.Nodes.Values.Any(n => n.Status == NodeStatus.Failed) ? RunStatus.Failed : RunStatus.Completed;
        }
        var decision = State.Artifacts.Latest("release-readiness")?["decision"]?.GetValue<string>();
        var m = Metrics();
        A(State.Status == RunStatus.Completed ? "run.completed" : "run.ended", null, "engine",
            $"Run {State.Status}{(State.StopReason is null ? "" : ": " + State.StopReason)}",
            new { status = State.Status.ToString(), releaseDecision = decision, metrics = m });
        Persist(final: true);
        return new RunResult(State.RunId, State.Status, State.StopReason, decision, State.RunDirectory, _ws.Root);
    }

    // ───────────────────────────── node execution ─────────────────────────────

    private async Task ExecuteNodeSafe(string id, CancellationToken kill)
    {
        var n = State.Nodes[id];
        try { await ExecuteNode(n, kill); }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            _ws.Rollback(id);
            lock (State.Sync) { n.Status = NodeStatus.Blocked; n.Error = "aborted by stop request (work rolled back)"; n.EndedAt = Now; }
            A("node.aborted", id, "engine", "Aborted by stop request; workspace changes rolled back");
        }
        catch (Exception ex)
        {
            _ws.Rollback(id);
            FailNode(n, "engine fault: " + ex.Message, halt: true);
        }
    }

    private async Task ExecuteNode(NodeState n, CancellationToken kill)
    {
        var spec = n.Spec;
        var id = spec.Id;
        A("node.started", id, "engine", $"{spec.Stage}/{spec.Agent} started (generation {n.Generation})", new { reads = n.InputHashes });

        foreach (var g in spec.EntryGates)
        {
            var r = await RunGate(g, "entry", n, null, _ws.ForNode(id, new AutonomyBoundary(Array.Empty<string>())));
            if (!r.Ok) { FailNode(n, $"entry gate '{g}' failed: {r.Reason}", halt: true); return; }
        }

        if (spec.Approval is { When: "before" } before)
        {
            var d = await Approve(n, before, "approval", PlannedChangeSummary(spec), null, kill);
            if (!d.Approved) { RejectNode(n, d); return; }
        }

        var agentName = spec.Agent;
        var prm = new Dictionary<string, string>(spec.Params);
        var fallback = false;
        var agentAttempt = 0;
        var clarRounds = 0;
        AgentResult? result;
        NodeWorkspace ws;

        while (true)
        {
            kill.ThrowIfCancellationRequested();
            agentAttempt++;
            lock (State.Sync) { n.Attempts++; State.AttemptsTotal++; }
            var agent = _agents.Get(agentName);
            ws = _ws.ForNode(id, agent.Autonomy);
            A("attempt.started", id, $"agent:{agentName}", $"attempt {agentAttempt}{(fallback ? " (fallback)" : "")}", new { agent = agentName, attempt = agentAttempt, fallback });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(kill);
                timeout.CancelAfter(_o.NodeTimeout);
                result = await agent.RunAsync(BuildContext(n, ws, agentAttempt, fallback, prm), timeout.Token);
                EnforcePolicy(n, ws);
                await CheckExitGates(n, result, ws);
                break;
            }
            catch (NeedsHumanException nh)
            {
                _ws.Rollback(id);
                lock (State.Sync) { n.Attempts--; State.AttemptsTotal--; }
                agentAttempt--;
                if (++clarRounds > _o.MaxClarificationRounds) { _ws.Rollback(id); FailNode(n, "clarification rounds exhausted", halt: true); return; }
                var askSpec = new ApprovalSpec("after", "medium", "Clarification required – the agent refuses to guess on high-severity ambiguities");
                var d = await Approve(n, askSpec, "clarification", nh.Human.Summary, nh.Human.Questions, kill);
                if (!d.Approved || d.Answers is not { Count: > 0 }) { RejectNode(n, d); return; }
                var merged = prm.TryGetValue("answers", out var prev) ? JsonSerializerDeserialize(prev) : new Dictionary<string, string>();
                foreach (var kv in d.Answers) merged[kv.Key] = kv.Value;
                prm["answers"] = Json.Serialize(merged);
                prm["revision"] = (clarRounds + 1).ToString();
                A("clarification.applied", id, $"human:{d.Approver}", $"{d.Answers.Count} answer(s) merged; agent re-runs with human input", new { answers = d.Answers });
                continue;
            }
            catch (CriticalPolicyException cp)
            {
                RollbackNode(n, id, $"critical policy violation {cp.Rule}");
                lock (State.Sync) State.AttemptsFailed++;
                FailNode(n, $"CRITICAL policy violation – safe-stop. {cp.Message}", halt: true);
                return;
            }
            catch (AutonomyViolationException av)
            {
                RecordViolation(n, "AUT-001", "critical", "", av.Message);
                RollbackNode(n, id, "autonomy boundary violation");
                lock (State.Sync) State.AttemptsFailed++;
                FailNode(n, "autonomy boundary violated – safe-stop. " + av.Message, halt: true);
                return;
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lock (State.Sync) { State.AttemptsFailed++; n.FirstFailureAt ??= Now; n.Error = ex.Message; }
                A("attempt.failed", id, $"agent:{agentName}", ex.Message, new { attempt = agentAttempt, fallback, error = ex.GetType().Name });
                RollbackNode(n, id, "failed attempt");
                bool canRetry;
                lock (State.Sync) canRetry = !fallback && agentAttempt <= spec.Retry.MaxRetries && State.TotalRetries < _o.MaxTotalRetries;
                if (canRetry)
                {
                    var backoff = TimeSpan.FromMilliseconds(spec.Retry.BackoffMs * Math.Pow(2, agentAttempt - 1));
                    lock (State.Sync) { n.Retries++; State.TotalRetries++; }
                    A("retry.scheduled", id, "engine", $"retry {agentAttempt}/{spec.Retry.MaxRetries} after {backoff.TotalMilliseconds:0} ms backoff", new { backoffMs = backoff.TotalMilliseconds });
                    await _o.Delay(backoff, kill);
                    continue;
                }
                if (!fallback && spec.Fallback is not null)
                {
                    fallback = true; agentName = spec.Fallback.Agent; agentAttempt = 0;
                    if (spec.Fallback.Params is not null) foreach (var kv in spec.Fallback.Params) prm[kv.Key] = kv.Value;
                    lock (State.Sync) n.UsedFallback = true;
                    A("fallback.engaged", id, "engine", $"retries exhausted → fallback agent '{agentName}'", new { fallbackAgent = agentName });
                    continue;
                }
                FailNode(n, $"retries and fallback exhausted: {ex.Message}", halt: true);
                return;
            }
        }

        // success path: change control, approval-after, atomic commit
        var changes = ws.ChangedFiles.Where(c => c.Op != "unchanged").ToList();
        if (State.Changes.Count + changes.Count > ChangeControl.MaxFilesChangedPerRun)
        {
            RecordViolation(n, "CHG-002", "critical", "", $"Blast radius exceeded: {State.Changes.Count + changes.Count} files > {ChangeControl.MaxFilesChangedPerRun}");
            RollbackNode(n, id, "blast radius exceeded");
            FailNode(n, "change-control blast radius exceeded – safe-stop", halt: true);
            return;
        }
        var afterSpec = spec.Approval is { When: "after" } a ? a : null;
        if (afterSpec is null)
        {
            var cc = ChangeControlReason(n, changes);
            if (cc is not null) afterSpec = new ApprovalSpec("after", "high", cc);
        }
        if (afterSpec is not null)
        {
            var d = await Approve(n, afterSpec, "approval", ResultSummary(result, changes), null, kill);
            if (!d.Approved) { RollbackNode(n, id, "approval rejected"); RejectNode(n, d); return; }
        }
        CommitNode(n, result, ws);
        Persist();
    }

    private static Dictionary<string, string> JsonSerializerDeserialize(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json.Options) ?? new();

    private AgentContext BuildContext(NodeState n, NodeWorkspace ws, int attempt, bool fallback, Dictionary<string, string> prm)
    {
        var s = n.Spec;
        var effective = new NodeSpec
        {
            Id = s.Id, Stage = s.Stage, Agent = s.Agent, Deps = s.Deps, Reads = s.Reads, EntryGates = s.EntryGates, ExitGates = s.ExitGates,
            Params = prm, Retry = s.Retry, Fallback = s.Fallback, Approval = s.Approval, Condition = s.Condition, Rollback = s.Rollback, ExpandedBy = s.ExpandedBy,
        };
        return new AgentContext
        {
            Node = effective, Attempt = attempt, IsFallback = fallback, Scenario = State.Scenario, Workspace = ws, Catalog = _catalog,
            Tests = _o.TestRunner, Time = _o.Time, Snapshot = new RunSnapshot(this), RunDirectory = State.RunDirectory,
            LogSink = m => A("agent.log", s.Id, $"agent:{s.Agent}", m),
            ArtifactReader = name =>
            {
                if (!s.Reads.Contains(name))
                {
                    RecordViolation(n, "AUT-002", "warn", "", $"Agent read artifact '{name}' that the node did not declare in Reads (returned empty)");
                    return null;
                }
                return State.Artifacts.Latest(name);
            },
        };
    }

    private void EnforcePolicy(NodeState n, NodeWorkspace ws)
    {
        var changed = ws.ChangedFiles.Where(c => c.Op is not ("delete" or "unchanged")).Select(c => c.Path).ToList();
        var findings = PolicyEngine.ScanFiles(changed, ws.ReadText);
        foreach (var f in findings) RecordViolation(n, f.Rule, f.Severity, f.File, $"{f.Message} (line {f.Line})");
        var crit = findings.FirstOrDefault(f => f.Severity == "critical");
        if (crit is not null) throw new CriticalPolicyException(crit.Rule, $"{crit.Rule} {crit.File}:{crit.Line} – {crit.Message}");
        var block = findings.Where(f => f.Severity == "block").ToList();
        if (block.Count > 0) throw new GateFailedException("policy-clean", string.Join("; ", block.Take(3).Select(f => $"{f.Rule} {f.File}:{f.Line}")));
    }

    private async Task CheckExitGates(NodeState n, AgentResult result, NodeWorkspace ws)
    {
        foreach (var g in n.Spec.ExitGates)
        {
            var r = await RunGate(g, "exit", n, result, ws);
            if (r.Human is not null) throw new NeedsHumanException(r.Human);
            if (!r.Ok) throw new GateFailedException(g, r.Reason);
        }
    }

    private async Task<GateResult> RunGate(string name, string phase, NodeState n, AgentResult? result, IWorkspace ws)
    {
        if (!_gates.TryGetValue(name, out var gate)) return GateResult.Fail($"unknown gate '{name}'");
        var r = await gate.CheckAsync(new GateContext
        {
            Node = n, Result = result, Workspace = ws, Catalog = _catalog,
            Artifact = a => result?.Artifacts.GetValueOrDefault(a)?.DeepClone() ?? State.Artifacts.Latest(a),
        });
        A($"gate.{phase}", n.Spec.Id, "engine", $"{name}: {(r.Ok ? "PASS" : r.Human is not null ? "NEEDS-HUMAN" : "FAIL")} – {r.Reason}", new { gate = name, ok = r.Ok });
        return r;
    }

    private void RollbackNode(NodeState n, string id, string why)
    {
        var restored = _ws.Rollback(id);
        if (restored > 0)
        {
            lock (State.Sync) n.Rollbacks++;
            A("rollback", id, "engine", $"Rolled back {restored} file operation(s) ({why}); workspace restored to pre-node state", new { files = restored, why });
        }
    }

    // ───────────────────────────── commit, expansion, re-planning ─────────────────────────────

    private void CommitNode(NodeState n, AgentResult result, NodeWorkspace ws)
    {
        var id = n.Spec.Id;
        lock (State.Sync)
        {
            if (n.Superseded || InputsChanged(n))
            {
                var restored = _ws.Rollback(id);
                if (restored > 0) n.Rollbacks++;
                n.Status = State.Nodes.TryGetValue(id, out var cur) && cur.Status == NodeStatus.Cancelled ? NodeStatus.Cancelled : NodeStatus.Stale;
                A("node.superseded", id, "engine", "Upstream input changed while this node ran – result discarded, node will re-run", new { restoredFiles = restored });
                return;
            }
            if (n.Status == NodeStatus.Cancelled) { _ws.Rollback(id); return; }

            _ws.Commit(id);
            n.CompletionSeq = ++State.CompletionCounter;
            foreach (var c in ws.ChangedFiles.Where(c => c.Op != "unchanged")) State.Changes.Add(new ChangeRecord(c.Path, c.Op, id));
            var basedOn = n.InputHashes.Where(kv => kv.Value != "∅").Select(kv => new ArtifactRef(kv.Key, kv.Value)).ToList();
            foreach (var (name, value) in result.Artifacts)
            {
                var meta = State.Artifacts.Put(name, value, id, Now);
                SaveArtifact(meta, value);
                A("artifact.committed", id, $"agent:{n.Spec.Agent}", $"{name} v{meta.Version}", new { name, meta.Version, meta.Hash, meta.Bytes });
            }
            foreach (var d in result.Decisions)
                State.Decisions.Add(new DecisionRecord($"D{++State.DecisionCounter}", id, d.Title, d.Rationale, d.Alternatives, d.Tradeoff, basedOn, Now));

            n.Status = NodeStatus.Done;
            n.EndedAt = Now;
            n.DurationMs = Math.Max(0, (n.EndedAt.Value - n.StartedAt!.Value).TotalMilliseconds - n.ApprovalWaitMs);
            n.Error = null;
            if (n.FirstFailureAt is { } ff) { State.RecoveryMs.Add((Now - ff).TotalMilliseconds); n.FirstFailureAt = null; }
            A("node.completed", id, $"agent:{n.Spec.Agent}", $"done in {n.DurationMs:0} ms ({n.Attempts} attempt(s), {n.Retries} retr{(n.Retries == 1 ? "y" : "ies")}, {n.Rollbacks} rollback(s))",
                new { attempts = n.Attempts, n.Retries, n.Rollbacks, n.UsedFallback, files = ws.ChangedFiles.Count });

            if (result.Expansion is not null) ApplyExpansion(id, result.Expansion);
            var change = FireEvents(id);
            Reconcile(change ?? $"artifacts of '{id}' changed");
        }
    }

    private bool InputsChanged(NodeState n) =>
        n.Spec.Reads.Any(r => (State.Artifacts.LatestHash(r) ?? "∅") != (n.InputHashes.GetValueOrDefault(r) ?? "∅"));

    private void ApplyExpansion(string expanderId, Expansion exp)
    {
        var newIds = exp.Nodes.Select(x => x.Id).ToHashSet();
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var spec in exp.Nodes)
        {
            spec.ExpandedBy = expanderId;
            if (State.Nodes.TryGetValue(spec.Id, out var existing) && existing.Status != NodeStatus.Cancelled) existing.Spec = spec;
            else { State.Nodes[spec.Id] = new NodeState { Spec = spec }; added.Add(spec.Id); }
        }
        foreach (var old in State.Nodes.Values.Where(x => x.Spec.ExpandedBy == expanderId && x.Status != NodeStatus.Cancelled && !newIds.Contains(x.Spec.Id)).ToList())
        {
            if (old.Status == NodeStatus.Running || old.Status == NodeStatus.AwaitingApproval) old.Superseded = true;
            else if (old.Status == NodeStatus.Done) { _ws.Rollback(old.Spec.Id); State.Changes.RemoveAll(c => c.NodeId == old.Spec.Id); }
            old.Status = NodeStatus.Cancelled;
            removed.Add(old.Spec.Id);
        }
        var cancelled = State.Nodes.Values.Where(x => x.Status == NodeStatus.Cancelled).Select(x => x.Spec.Id).ToHashSet();
        foreach (var target in exp.JoinInto)
            if (State.Nodes.TryGetValue(target, out var t))
                foreach (var id in newIds) if (!t.Spec.Deps.Contains(id)) t.Spec.Deps.Add(id);
        foreach (var x in State.Nodes.Values) x.Spec.Deps.RemoveAll(cancelled.Contains);
        BuildGraph().Validate();
        A("graph.expanded", expanderId, "engine", $"Plan expanded by '{expanderId}': +{added.Count} node(s) [{string.Join(", ", added)}], -{removed.Count} [{string.Join(", ", removed)}]",
            new { added, removed, layers = BuildGraph().Layers() });
    }

    private string? FireEvents(string completedNodeId)
    {
        string? fired = null;
        for (var i = 0; i < State.Scenario.Events.Count; i++)
        {
            var ev = State.Scenario.Events[i];
            if (ev.AfterNode != completedNodeId || !State.FiredEvents.Add(i)) continue;
            fired = $"requirement change request: {ev.Description}";
            var cur = State.Artifacts.Latest("requirements")?.Deserialize<RequirementsDoc>();
            if (cur is null) continue;
            var feats = cur.Features.Where(f => !ev.RemoveFeatures.Contains(f.Id)).ToList();
            foreach (var fid in ev.AddFeatures)
                if (feats.All(f => f.Id != fid) && _catalog.Features.TryGetValue(fid, out var def))
                    feats.Add(new FeatureReq(fid, def.Title, "amended", $"Change request: {ev.Description}"));
            var amended = cur with { Features = feats, RawText = cur.RawText + (ev.AppendText is null ? "" : "\n" + ev.AppendText), Revision = cur.Revision + 1 };
            var meta = State.Artifacts.Put("requirements", Json.ToNode(amended), "human:change-request", Now);
            SaveArtifact(meta, Json.ToNode(amended));
            A("requirement.amended", null, "human:change-request", $"CHANGE REQUEST after '{completedNodeId}': {ev.Description}", new { ev.AddFeatures, ev.RemoveFeatures, meta.Hash });
            State.Decisions.Add(new DecisionRecord($"D{++State.DecisionCounter}", "human:change-request", "Mid-run requirement change", ev.Description,
                new() { "Ignore until next release", "Restart the whole run" }, "Re-plan only the invalidated part of the graph",
                new() { new ArtifactRef("requirements", meta.Hash) }, Now));
        }
        return fired;
    }

    /// <summary>Content-addressed re-planning: any Done node whose consumed artifact hashes no longer match is reverted and re-queued; running nodes are superseded.</summary>
    private void Reconcile(string reason)
    {
        var stale = new List<NodeState>();
        var superseded = new List<string>();
        foreach (var n in State.Nodes.Values)
        {
            if (n.Status is not (NodeStatus.Done or NodeStatus.Running or NodeStatus.AwaitingApproval) || !InputsChanged(n)) continue;
            if (n.Status == NodeStatus.Done) stale.Add(n); else { n.Superseded = true; superseded.Add(n.Spec.Id); }
        }
        foreach (var n in stale.OrderByDescending(x => x.CompletionSeq))
        {
            var restored = _ws.Rollback(n.Spec.Id);
            if (restored > 0) n.Rollbacks++;
            State.Changes.RemoveAll(c => c.NodeId == n.Spec.Id);
            n.Status = NodeStatus.Stale; n.EndedAt = null;
            A("node.stale", n.Spec.Id, "engine", "Consumed artifact changed upstream → reverted and queued for re-execution", new { revertedFiles = restored });
        }
        if (stale.Count == 0 && superseded.Count == 0) return;
        State.Replans.Add(new ReplanRecord(Now, reason, stale.Select(s => s.Spec.Id).Concat(superseded).ToList()));
        A("replan.triggered", null, "engine", $"RE-PLAN ({reason}): {stale.Count} node(s) invalidated, {superseded.Count} in-flight node(s) superseded; unaffected nodes keep their results",
            new { stale = stale.Select(s => s.Spec.Id), superseded });
    }

    // ───────────────────────────── approvals, failure handling ─────────────────────────────

    private async Task<ApprovalDecision> Approve(NodeState n, ApprovalSpec spec, string kind, string summary, List<OpenQuestion>? questions, CancellationToken kill)
    {
        ApprovalRequest req;
        lock (State.Sync)
        {
            req = new ApprovalRequest($"{State.RunId}-a{Interlocked.Increment(ref _approvalSeq)}", State.RunId, n.Spec.Id, kind, spec.When, spec.Risk, spec.Reason, summary, questions ?? new(), Now);
            State.Pending.Add(req);
            n.Status = NodeStatus.AwaitingApproval;
            State.Status = RunStatus.AwaitingHuman;
        }
        A("approval.requested", n.Spec.Id, "policy", $"{kind} required ({spec.Risk}, {spec.When}): {spec.Reason}", new { req.Id, kind, spec.When, spec.Risk, summary });
        var t0 = Now;
        ApprovalDecision d;
        try { d = await _o.Approvals.DecideAsync(req, kill); }
        finally
        {
            lock (State.Sync)
            {
                State.Pending.Remove(req);
                if (n.Status == NodeStatus.AwaitingApproval) n.Status = NodeStatus.Running;
                n.ApprovalWaitMs += (Now - t0).TotalMilliseconds;
                if (State.Pending.Count == 0 && State.Status == RunStatus.AwaitingHuman) State.Status = RunStatus.Running;
            }
        }
        lock (State.Sync)
        {
            State.Approvals.Add(new ApprovalRecord(req.Id, n.Spec.Id, kind, spec.When, spec.Risk, spec.Reason, d.Approved ? "approved" : "rejected", d.Approver, d.Comment, Now, (Now - t0).TotalMilliseconds));
            State.Decisions.Add(new DecisionRecord($"D{++State.DecisionCounter}", n.Spec.Id, $"{(d.Approved ? "Approved" : "Rejected")} {kind}: {spec.Reason}",
                $"{d.Approver}: {d.Comment}", new() { d.Approved ? "Reject" : "Approve" }, "Human retains final authority over high-impact actions",
                n.InputHashes.Where(kv => kv.Value != "∅").Select(kv => new ArtifactRef(kv.Key, kv.Value)).ToList(), Now));
        }
        A("approval.decided", n.Spec.Id, $"human:{d.Approver}", $"{(d.Approved ? "APPROVED" : "REJECTED")} – {d.Comment}", new { req.Id, d.Approved, d.Comment, answers = d.Answers });
        return d;
    }

    private void RejectNode(NodeState n, ApprovalDecision d)
    {
        _ws.Rollback(n.Spec.Id);
        lock (State.Sync)
        {
            n.Status = NodeStatus.Rejected; n.EndedAt = Now; n.Error = $"rejected by {d.Approver}: {d.Comment}";
            State.Halted = true; State.RejectedByHuman = true; State.StopReason ??= $"Human rejected '{n.Spec.Id}' ({d.Approver}: {d.Comment})";
        }
        A("node.rejected", n.Spec.Id, $"human:{d.Approver}", n.Error!);
        A("safe-stop", n.Spec.Id, "engine", State.StopReason!);
    }

    private void FailNode(NodeState n, string reason, bool halt)
    {
        lock (State.Sync)
        {
            n.Status = NodeStatus.Failed; n.Error = reason; n.EndedAt = Now;
            if (n.StartedAt is { } s) n.DurationMs = Math.Max(0, (n.EndedAt.Value - s).TotalMilliseconds - n.ApprovalWaitMs);
            if (halt) { State.Halted = true; State.StopReason ??= $"'{n.Spec.Id}': {reason}"; }
        }
        A("node.failed", n.Spec.Id, "engine", reason);
        if (halt) A("safe-stop", n.Spec.Id, "engine", $"SAFE-STOP: {State.StopReason}. Workspace and audit trail preserved; run is resumable.");
    }

    private void RecordViolation(NodeState n, string rule, string severity, string file, string message)
    {
        lock (State.Sync) State.Violations.Add(new PolicyViolation(rule, severity, n.Spec.Id, file, message, Now));
        A("policy.violation", n.Spec.Id, "policy", $"{rule} [{severity}] {file} {message}".Trim(), new { rule, severity, file });
    }

    private string? ChangeControlReason(NodeState n, IReadOnlyList<FileChange> changes)
    {
        if (n.Spec.Approval is not null) return null;
        bool Generated(string p) => p == _catalog.Core.RegistryPath || p == ".gitignore" || p == "README.md" || p == "CHANGELOG.md" || p.StartsWith("docs/");
        var modified = changes.Where(c => c.Op is "modify" or "delete" && !Generated(c.Path)).Select(c => c.Path).ToList();
        if (modified.Count == 0) return null;
        var sensitive = modified.Where(ChangeControl.IsSensitivePath).ToList();
        return $"CHG-001 change control: node modifies existing file(s) {string.Join(", ", modified)}" + (sensitive.Count > 0 ? $" (security-sensitive: {string.Join(", ", sensitive)})" : "");
    }

    private string PlannedChangeSummary(NodeSpec spec)
    {
        var lines = new List<string> { $"Node: {spec.Id} (agent {spec.Agent})", $"Why approval: {spec.Approval?.Reason}" };
        if (spec.Params.TryGetValue("feature", out var f) && _catalog.Features.TryGetValue(f, out var def))
        {
            lines.Add($"Feature: {def.Id} – {def.Title}");
            if (def.Sensitive) lines.Add($"Sensitive: {def.SensitiveReason ?? "yes"}");
            if (def.Modifies.Count > 0) lines.Add("Will MODIFY existing files: " + string.Join(", ", def.Modifies));
            var files = _catalog.FeatureFiles(f);
            lines.Add($"Will write {files.Count} file(s):");
            lines.AddRange(files.Take(12).Select(p => "  " + p));
            lines.Add("Acceptance criteria: " + string.Join(", ", def.Acceptance.Select(a => a.Id)));
        }
        return string.Join("\n", lines);
    }

    private static string ResultSummary(AgentResult result, IReadOnlyList<FileChange> changes) =>
        $"Artifacts produced: {string.Join(", ", result.Artifacts.Keys)}\n" +
        (changes.Count > 0 ? "Workspace changes:\n" + ChangeControl.Describe(changes) + "\n" : "") +
        (result.Decisions.Count > 0 ? "Decisions:\n" + string.Join("\n", result.Decisions.Take(8).Select(d => $"  - {d.Title}: {d.Rationale}")) : "");

    // ───────────────────────────── persistence ─────────────────────────────

    private void A(string type, string? nodeId, string actor, string message, object? data = null) => Audit.Append(type, nodeId, actor, message, data);

    private void SaveArtifact(ArtifactMeta meta, JsonNode value)
    {
        try
        {
            var dir = Path.Combine(State.RunDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{meta.Name.Replace(':', '_')}@v{meta.Version}.json"), value.ToJsonString(Json.Pretty));
        }
        catch (IOException) { /* best effort */ }
    }

    private readonly object _persistLock = new();
    private void Persist(bool final = false)
    {
        try
        {
            lock (_persistLock)
            {
                var view = RunViews.Build(this);
                File.WriteAllText(Path.Combine(State.RunDirectory, "state.json"), Json.Serialize(view, pretty: true));
                if (final) File.WriteAllText(Path.Combine(State.RunDirectory, "metrics.json"), Json.Serialize(Metrics(), pretty: true));
            }
        }
        catch (IOException) { /* best effort */ }
    }
}
