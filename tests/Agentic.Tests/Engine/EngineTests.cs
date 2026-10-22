using Agentic.Orchestrator;

namespace Agentic.Tests.EngineTests;

public class EngineOrchestrationTests
{
    [Fact]
    public async Task Independent_paths_run_in_parallel_and_join_in_order()
    {
        var order = new List<string>();
        var started = 0;
        var both = new TaskCompletionSource();
        async Task<AgentResult> Par(AgentContext c)
        {
            lock (order) order.Add("start:" + c.Node.Id);
            if (Interlocked.Increment(ref started) == 2) both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5)); // only completes if B and C really overlap
            lock (order) order.Add("end:" + c.Node.Id);
            return Kit.Ok((c.Node.Id, new { ok = true }));
        }
        var plan = new[] { Kit.Node("a", "step"), Kit.Node("b", "par", "a"), Kit.Node("c", "par", "a"), Kit.Node("d", "step", "b", "c") };
        var e = Kit.Create(Kit.Scenario(), plan, new IAgent[]
        {
            Kit.Sync("step", c => { lock (order) order.Add("run:" + c.Node.Id); return Kit.Ok((c.Node.Id, new { ok = true })); }),
            Kit.Agent("par", Par),
        });

        var r = await e.RunAsync();

        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.True(order.IndexOf("run:d") > order.IndexOf("end:b") && order.IndexOf("run:d") > order.IndexOf("end:c"), "join must wait for both branches");
        Assert.True(order.IndexOf("run:a") < order.IndexOf("start:b"));
        Assert.Equal(1.0, e.Metrics().SuccessRate);
        Assert.True(e.Audit.Verify().Valid);
    }

    [Fact]
    public async Task Declared_reads_pass_artifacts_between_nodes_and_undeclared_reads_are_denied()
    {
        string? seen = null; bool denied = false;
        var up = Kit.Node("up", "producer");
        var down = Kit.Node("down", "consumer", "up"); down.Reads.Add("x");
        var e = Kit.Create(Kit.Scenario(), new[] { up, down }, new IAgent[]
        {
            Kit.Sync("producer", _ => Kit.Ok(("x", new { v = "hello" }), ("secret", new { v = "no" }))),
            Kit.Sync("consumer", c => { seen = c.Read<System.Text.Json.Nodes.JsonObject>("x")["v"]!.GetValue<string>(); denied = !c.Has("secret"); return Kit.Ok(); }),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal("hello", seen);
        Assert.True(denied);
        Assert.Contains(e.State.Violations, v => v.Rule == "AUT-002");
    }

    [Fact]
    public async Task Failed_attempt_is_rolled_back_then_retried_and_recovery_is_measured()
    {
        var calls = 0;
        var node = Kit.Node("a", "flaky"); node.Retry = new RetryPolicy(2, 10);
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[]
        {
            Kit.Sync("flaky", c =>
            {
                c.Workspace.WriteText("src/a.txt", $"attempt {++calls}");
                if (calls == 1) throw new InvalidOperationException("transient");
                return Kit.Ok(("out", new { files = new[] { "src/a.txt" } }));
            }, "src/**"),
        });
        var r = await e.RunAsync();
        var n = Kit.N(e, "a");
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(2, n.Attempts); Assert.Equal(1, n.Retries); Assert.Equal(1, n.Rollbacks);
        Assert.Equal("attempt 2", File.ReadAllText(Path.Combine(e.WorkspaceRoot, "src/a.txt")));
        var m = e.Metrics();
        Assert.Equal(0.5, m.AttemptSuccessRate);
        Assert.NotNull(m.MttrMs);
        Assert.Contains(e.Audit.Entries, x => x.Type == "retry.scheduled");
        Assert.Contains(e.Audit.Entries, x => x.Type == "rollback");
    }

    [Fact]
    public async Task Exhausted_retries_engage_the_fallback_agent()
    {
        var node = Kit.Node("a", "primary"); node.Retry = new RetryPolicy(1, 0); node.Fallback = new FallbackSpec("backup");
        int primary = 0;
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[]
        {
            Kit.Sync("primary", _ => { primary++; throw new InvalidOperationException("always"); }),
            Kit.Sync("backup", _ => Kit.Ok(("out", new { by = "backup" }))),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(2, primary);
        Assert.True(Kit.N(e, "a").UsedFallback);
        Assert.Equal(1, e.Metrics().Fallbacks);
        Assert.Contains(e.Audit.Entries, x => x.Type == "fallback.engaged");
    }

    [Fact]
    public async Task Total_failure_safe_stops_blocks_dependents_preserves_done_work_and_is_resumable()
    {
        var broken = true; int runsA = 0, runsB = 0, runsC = 0;
        var b = Kit.Node("b", "b", "a"); b.Retry = new RetryPolicy(1, 0);
        var e = Kit.Create(Kit.Scenario(), new[] { Kit.Node("a", "a"), b, Kit.Node("c", "c", "b") }, new IAgent[]
        {
            Kit.Sync("a", _ => { runsA++; return Kit.Ok(("a", new { v = 1 })); }),
            Kit.Sync("b", c => { runsB++; c.Workspace.WriteText("src/b.txt", "partial"); if (broken) throw new InvalidOperationException("boom"); return Kit.Ok(("b", new { v = 1 })); }, "src/**"),
            Kit.Sync("c", _ => { runsC++; return Kit.Ok(("c", new { v = 1 })); }),
        });

        var r1 = await e.RunAsync();
        Assert.Equal(RunStatus.SafeStopped, r1.Status);
        Assert.Equal(NodeStatus.Failed, Kit.N(e, "b").Status);
        Assert.Equal(NodeStatus.Blocked, Kit.N(e, "c").Status);
        Assert.Equal(0, runsC);
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/b.txt")), "failed node leaves no partial files");
        Assert.Contains("safe-stop", e.Audit.Entries.Select(x => x.Type));

        broken = false;
        var r2 = await e.ResumeAsync();
        Assert.Equal(RunStatus.Completed, r2.Status);
        Assert.Equal(1, runsA);       // finished work is not repeated
        Assert.Equal(1, runsC);
        Assert.True(File.Exists(Path.Combine(e.WorkspaceRoot, "src/b.txt")));
        Assert.True(e.Audit.Verify().Valid);
    }

    [Fact]
    public async Task Critical_policy_violation_stops_immediately_without_retry_and_rolls_back()
    {
        var node = Kit.Node("a", "leaky"); node.Retry = new RetryPolicy(5, 0);
        var e = Kit.Create(Kit.Scenario(), new[] { node, Kit.Node("b", "ok", "a") }, new IAgent[]
        {
            Kit.Sync("leaky", c => { c.Workspace.WriteText("src/k.cs", "class K { string k = \"AKIAIOSFODNN7EXAMPLE\"; }"); return Kit.Ok(); }, "src/**"),
            Kit.Sync("ok", _ => Kit.Ok()),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Equal(1, Kit.N(e, "a").Attempts);
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/k.cs")));
        Assert.Contains(e.State.Violations, v => v.Rule == "SEC-001" && v.Severity == "critical");
        Assert.Equal(NodeStatus.Blocked, Kit.N(e, "b").Status);
    }

    [Fact]
    public async Task Blocking_policy_violation_fails_the_attempt_and_retry_can_fix_it()
    {
        var calls = 0;
        var node = Kit.Node("a", "agent"); node.Retry = new RetryPolicy(1, 0);
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[]
        {
            Kit.Sync("agent", c => { c.Workspace.WriteText("src/UrlShortener.Api/Core/G.cs", ++calls == 1 ? "class G { object r = new Random(); }" : "class G { }"); return Kit.Ok(); }, "src/**"),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(2, calls);
        Assert.Contains(e.State.Violations, v => v.Rule == "SEC-004");
    }

    [Fact]
    public async Task Autonomy_boundary_violation_is_critical()
    {
        var node = Kit.Node("docs", "writer"); node.Retry = new RetryPolicy(3, 0);
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[]
        {
            Kit.Sync("writer", c => { c.Workspace.WriteText("src/app.cs", "x"); return Kit.Ok(); }, "docs/**"),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Equal(1, Kit.N(e, "docs").Attempts);
        Assert.Contains(e.State.Violations, v => v.Rule == "AUT-001");
    }

    [Fact]
    public async Task Blast_radius_limit_halts_the_run()
    {
        var e = Kit.Create(Kit.Scenario(), new[] { Kit.Node("a", "spam") }, new IAgent[]
        {
            Kit.Sync("spam", c => { for (var i = 0; i <= ChangeControl.MaxFilesChangedPerRun; i++) c.Workspace.WriteText($"src/f{i}.txt", "x"); return Kit.Ok(); }, "src/**"),
        });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Contains(e.State.Violations, v => v.Rule == "CHG-002");
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/f0.txt")));
    }

    [Fact]
    public async Task Entry_gate_failure_stops_the_run()
    {
        var node = Kit.Node("a", "x"); node.EntryGates.Add("workspace-ready");
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[] { Kit.Sync("x", _ => Kit.Ok()) });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Contains("entry gate", Kit.N(e, "a").Error);
    }

    [Fact]
    public async Task Conditional_nodes_are_skipped_and_do_not_block_dependents()
    {
        var cond = Kit.Node("analysis", "x"); cond.Condition = "workspace:baseline";
        int ran = 0;
        var e = Kit.Create(Kit.Scenario(workspace: "empty"), new[] { cond, Kit.Node("next", "y", "analysis") },
            new IAgent[] { Kit.Sync("x", _ => Kit.Ok()), Kit.Sync("y", _ => { ran++; return Kit.Ok(); }) });
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(NodeStatus.Skipped, Kit.N(e, "analysis").Status);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Kill_switch_aborts_inflight_work_and_safe_stops()
    {
        var running = new TaskCompletionSource();
        var e = Kit.Create(Kit.Scenario(), new[] { Kit.Node("a", "slow") }, new IAgent[]
        {
            Kit.AgentCt("slow", async (c, ct) => { c.Workspace.WriteText("src/partial.txt", "x"); running.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Kit.Ok(); }, "src/**"),
        });
        var task = e.RunAsync();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        e.RequestStop("operator pressed stop");
        var r = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/partial.txt")), "in-flight work is rolled back");
        Assert.Contains(e.Audit.Entries, x => x.Type == "run.stop-requested");
    }
}

public class EngineGovernanceTests
{
    [Fact]
    public async Task Before_approval_rejection_prevents_execution_and_marks_run_rejected()
    {
        int ran = 0;
        var node = Kit.Node("risky", "x"); node.Approval = new ApprovalSpec("before", "high", "touches production config");
        var approvals = new FuncApprovals(_ => new ApprovalDecision(false, "alice", "not now"));
        var e = Kit.Create(Kit.Scenario(), new[] { node, Kit.Node("after", "x", "risky") }, new IAgent[] { Kit.Sync("x", _ => { ran++; return Kit.Ok(); }) }, approvals);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Rejected, r.Status);
        Assert.Equal(0, ran);
        Assert.Equal(NodeStatus.Rejected, Kit.N(e, "risky").Status);
        Assert.Equal(NodeStatus.Blocked, Kit.N(e, "after").Status);
        Assert.Equal(1, e.Metrics().ApprovalsRejected);
        Assert.Contains(e.Audit.Entries, x => x.Type == "approval.decided" && x.Actor == "human:alice");
    }

    [Fact]
    public async Task After_approval_rejection_rolls_back_the_work()
    {
        var node = Kit.Node("a", "writer"); node.Approval = new ApprovalSpec("after", "high", "release");
        var approvals = new FuncApprovals(_ => new ApprovalDecision(false, "bob", "quality not sufficient"));
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[] { Kit.Sync("writer", c => { c.Workspace.WriteText("src/x.txt", "x"); return Kit.Ok(("out", new { v = 1 })); }, "src/**") }, approvals);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Rejected, r.Status);
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/x.txt")));
        Assert.Null(e.State.Artifacts.Latest("out"));
    }

    [Fact]
    public async Task Approved_checkpoint_is_recorded_in_lineage_and_metrics()
    {
        var node = Kit.Node("a", "x"); node.Approval = new ApprovalSpec("after", "medium", "baseline sign-off");
        var approvals = new FuncApprovals(_ => new ApprovalDecision(true, "carol", "looks right"));
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[] { Kit.Sync("x", _ => Kit.Ok(("o", new { v = 1 }))) }, approvals);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        var m = e.Metrics();
        Assert.Equal(1, m.ApprovalsRequested); Assert.Equal(1, m.ApprovalsGranted);
        Assert.Contains(e.State.Decisions, d => d.Title.StartsWith("Approved approval") && d.Rationale.Contains("carol"));
        Assert.Single(approvals.Requests);
    }

    [Fact]
    public async Task Modifying_an_existing_file_without_explicit_approval_triggers_change_control()
    {
        var node = Kit.Node("a", "editor");
        var approvals = new FuncApprovals(r => new ApprovalDecision(false, "dave", "no"));
        var e = Kit.Create(Kit.Scenario(), new[] { node }, new IAgent[] { Kit.Sync("editor", c => { c.Workspace.WriteText("src/exist.txt", "changed"); return Kit.Ok(); }, "src/**") }, approvals);
        Directory.CreateDirectory(Path.Combine(e.WorkspaceRoot, "src"));
        File.WriteAllText(Path.Combine(e.WorkspaceRoot, "src/exist.txt"), "original");

        var r = await e.RunAsync();

        Assert.Equal(RunStatus.Rejected, r.Status);
        Assert.Contains("CHG-001", approvals.Requests.Single().Reason);
        Assert.Equal("original", File.ReadAllText(Path.Combine(e.WorkspaceRoot, "src/exist.txt")));
    }

    [Fact]
    public async Task Creating_new_files_does_not_need_approval()
    {
        var approvals = new FuncApprovals(_ => new ApprovalDecision(false, "x", "no"));
        var e = Kit.Create(Kit.Scenario(), new[] { Kit.Node("a", "w") }, new IAgent[] { Kit.Sync("w", c => { c.Workspace.WriteText("src/new.txt", "n"); return Kit.Ok(); }, "src/**") }, approvals);
        Assert.Equal(RunStatus.Completed, (await e.RunAsync()).Status);
        Assert.Empty(approvals.Requests);
    }

    [Fact]
    public async Task Clarification_loop_asks_the_human_then_reruns_the_agent_with_answers()
    {
        var seenAnswers = new List<string?>();
        var req = Kit.Node("requirements", "req"); req.ExitGates.Add("requirements-clear");
        var approvals = new FuncApprovals(r => r.Kind == "clarification"
            ? new ApprovalDecision(true, "pm", "answered", new() { ["AMB-X"] = "p95 under 50 ms" })
            : new ApprovalDecision(true, "pm", "ok"));
        var e = Kit.Create(Kit.Scenario(), new[] { req }, new IAgent[]
        {
            Kit.Sync("req", c =>
            {
                c.Params.TryGetValue("answers", out var a);
                seenAnswers.Add(a);
                return Kit.Ok(("requirements", Kit.Requirements(a is null ? "needs-clarification" : "clear", new[] { "shorten", "redirect" }, answered: a is not null)));
            }),
        }, approvals);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(2, seenAnswers.Count);
        Assert.Null(seenAnswers[0]);
        Assert.Contains("p95 under 50 ms", seenAnswers[1]);
        Assert.Contains(e.Audit.Entries, x => x.Type == "clarification.applied");
        Assert.Equal(0, Kit.N(e, "requirements").Retries);   // asking a human is not a failure
        Assert.Equal(0, e.Metrics().Retries);
    }

    [Fact]
    public async Task Unanswered_clarification_safe_stops_instead_of_guessing()
    {
        var req = Kit.Node("requirements", "req"); req.ExitGates.Add("requirements-clear");
        var approvals = new FuncApprovals(_ => new ApprovalDecision(false, "pm", "no time"));
        var e = Kit.Create(Kit.Scenario(), new[] { req, Kit.Node("design", "d", "requirements") }, new IAgent[]
        {
            Kit.Sync("req", _ => Kit.Ok(("requirements", Kit.Requirements("needs-clarification", new[] { "shorten", "redirect" })))),
            Kit.Sync("d", _ => Kit.Ok()),
        }, approvals);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Rejected, r.Status);
        Assert.Equal(NodeStatus.Blocked, Kit.N(e, "design").Status);
    }
}

public class EngineReplanTests
{
    private static (List<NodeSpec> plan, List<IAgent> agents, Dictionary<string, int> runs) Fixture(string[] features)
    {
        var runs = new Dictionary<string, int>();
        void Count(string id) { lock (runs) runs[id] = runs.GetValueOrDefault(id) + 1; }
        var plan = new List<NodeSpec>
        {
            Kit.Node("requirements", "req"),
            new() { Id = "design", Agent = "arch", Stage = "design", Deps = { "requirements" }, Reads = { "requirements" }, Retry = new RetryPolicy(0, 0) },
            new() { Id = "integrate", Agent = "integ", Stage = "integration", Deps = { "design" }, Reads = { "design" }, Retry = new RetryPolicy(0, 0) },
        };
        var agents = new List<IAgent>
        {
            Kit.Sync("req", c => { Count(c.Node.Id); return Kit.Ok(("requirements", Kit.Requirements("clear", features, true))); }),
            Kit.Sync("arch", c =>
            {
                Count(c.Node.Id);
                var feats = c.Read<RequirementsDoc>("requirements").Features.Select(f => f.Id).ToList();
                var res = Kit.Ok(("design", new { features = feats }));
                var nodes = new List<NodeSpec>();
                string? prev = null;
                foreach (var f in feats)
                {
                    res.Add($"task:{f}", new { feature = f });
                    var n = new NodeSpec { Id = $"implement:{f}", Agent = "impl", Stage = "implementation", Deps = { "design" }, Reads = { $"task:{f}" }, Params = { ["feature"] = f }, Retry = new RetryPolicy(0, 0) };
                    if (prev is not null) n.Deps.Add(prev); // serialise so removal of a not-yet-started node is deterministic
                    prev = n.Id;
                    nodes.Add(n);
                }
                res.Expansion = new Expansion(nodes, new() { "integrate" });
                return res;
            }),
            Kit.Sync("impl", c => { Count(c.Node.Id); var f = c.Param("feature"); c.Workspace.WriteText($"src/{f}.txt", $"impl of {f}"); return Kit.Ok(($"impl:{f}", new { files = new[] { $"src/{f}.txt" } })); }, "src/**"),
            Kit.Sync("integ", c => { Count(c.Node.Id); c.Workspace.WriteText("src/registry.txt", string.Join(",", c.Read<System.Text.Json.Nodes.JsonObject>("design")["features"]!.AsArray().Select(x => x!.GetValue<string>()))); return Kit.Ok(); }, "src/**"),
        };
        return (plan, agents, runs);
    }

    [Fact]
    public async Task Requirement_change_replans_only_the_invalidated_part_of_the_graph()
    {
        var (plan, agents, runs) = Fixture(new[] { "alias", "manage" });
        var scenario = Kit.Scenario();
        scenario.Events.Add(new ScenarioEvent("implement:alias", "add expiry, drop manage", new[] { "expiry" }, new[] { "manage" }, null));
        var e = Kit.Create(scenario, plan, agents);

        var r = await e.RunAsync();

        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(2, runs["design"]);                       // re-planned
        Assert.Equal(1, runs["implement:alias"]);              // unaffected: result kept, NOT re-run
        Assert.Equal(1, runs["implement:expiry"]);             // newly added by the re-plan
        Assert.False(runs.ContainsKey("implement:manage"));    // cancelled before it ever started
        Assert.Equal(NodeStatus.Cancelled, Kit.N(e, "implement:manage").Status);
        Assert.Equal(1, runs["integrate"]);                    // waited for the re-plan, ran once with the final feature set
        Assert.Equal("alias,expiry", File.ReadAllText(Path.Combine(e.WorkspaceRoot, "src/registry.txt")));
        Assert.True(e.State.Replans.Count >= 1);
        Assert.Contains(e.Audit.Entries, x => x.Type == "requirement.amended");
        Assert.Contains(e.Audit.Entries, x => x.Type == "graph.expanded" && x.Message.Contains("implement:expiry"));
        Assert.True(e.Audit.Verify().Valid);
    }

    [Fact]
    public async Task Removing_a_completed_feature_reverts_its_files()
    {
        var (plan, agents, runs) = Fixture(new[] { "alias", "manage" });
        var scenario = Kit.Scenario();
        scenario.Events.Add(new ScenarioEvent("implement:alias", "drop alias again", Array.Empty<string>(), new[] { "alias" }, null));
        var e = Kit.Create(scenario, plan, agents);

        var r = await e.RunAsync();

        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(NodeStatus.Cancelled, Kit.N(e, "implement:alias").Status);
        Assert.False(File.Exists(Path.Combine(e.WorkspaceRoot, "src/alias.txt")), "completed work of a dropped feature is reverted");
        Assert.True(File.Exists(Path.Combine(e.WorkspaceRoot, "src/manage.txt")));
        Assert.DoesNotContain(e.State.Changes, c => c.Path == "src/alias.txt");
        Assert.Equal("manage", File.ReadAllText(Path.Combine(e.WorkspaceRoot, "src/registry.txt")));
    }

    [Fact]
    public async Task Without_events_the_expansion_joins_all_features_into_integrate()
    {
        var (plan, agents, runs) = Fixture(new[] { "alias", "manage", "expiry" });
        var e = Kit.Create(Kit.Scenario(), plan, agents);
        var r = await e.RunAsync();
        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(new[] { "design", "implement:alias", "implement:expiry", "implement:manage" }, e.State.Nodes["integrate"].Spec.Deps.OrderBy(x => x));
        Assert.Empty(e.State.Replans);
        Assert.Equal(1, runs["design"]);
    }

    [Fact]
    public async Task Metrics_report_critical_path_and_stage_latency()
    {
        var (plan, agents, _) = Fixture(new[] { "alias", "manage" });
        var e = Kit.Create(Kit.Scenario(), plan, agents);
        await e.RunAsync();
        var m = e.Metrics();
        Assert.Equal(1.0, m.SuccessRate);
        Assert.True(m.EndToEndMs >= 0);
        Assert.Contains("integrate", m.CriticalPath);
        Assert.Contains("implementation", m.PerStage.Keys);
        Assert.Equal(m.NodesDone, m.NodesTotal);
    }
}
