using System.Text;
using System.Text.Json.Nodes;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Writes the "Final Engineering Summary" (ENGINEERING_SUMMARY.md) into the run directory. It consumes run-level evidence (metrics, state, lineage)
/// plus every artifact that exists – all optional, so a safe-stopped run still gets an honest summary of what happened.
/// Section order is fixed: Outcome, Plan and rationale, Artifacts, Risks, Assumptions, Orchestration evidence, Decision lineage, Limitations.
/// </summary>
public sealed class SummarizerAgent : IAgent
{
    public const string AgentName = "summarizer";
    public const string FileName = "ENGINEERING_SUMMARY.md";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => new(Array.Empty<string>(), ReadsRunSnapshot: true);

    private static readonly string[] KnownArtifacts =
        { "requirements", "impact", "repro", "design", "test-plan", "integration", "test-report", "traceability", "security-report", "docs", "release-readiness" };

    private sealed record NodeView(string Id, string Status, string Stage, string Agent, List<string> Deps, JsonNode Raw);

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var snap = ctx.Snapshot;
        var s = new Evidence(ctx, snap?.Metrics(), snap?.State(), snap?.Lineage());

        var sb = new StringBuilder();
        sb.AppendLine("# Final Engineering Summary").AppendLine();
        Outcome(sb, s);
        Plan(sb, s);
        Artifacts(sb, s);
        Risks(sb, s);
        Assumptions(sb, s);
        Orchestration(sb, s);
        Lineage(sb, s);
        Limitations(sb, s);

        Directory.CreateDirectory(ctx.RunDirectory);
        var path = Path.GetFullPath(Path.Combine(ctx.RunDirectory, FileName));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        var bytes = new FileInfo(path).Length;
        ctx.Log($"summary written: {path} ({bytes} bytes)");

        return new AgentResult().Add("summary", new { path, bytes })
            .Decide("Write the engineering summary from recorded evidence",
                $"Rendered {FileName} ({bytes} bytes) from the run's metrics, state and decision lineage and {s.AvailableArtifacts.Count} artifact(s); sections whose evidence does not exist are marked as such instead of being omitted.",
                new[] { "Free-form narrative written by a language model (not reproducible)", "Dump raw JSON (not reviewable)" },
                "Deterministic rendering is reproducible offline and traceable to artifacts, at the cost of less narrative flexibility.");
    }

    // ───────────── evidence bag ─────────────

    private sealed class Evidence
    {
        public AgentContext Ctx { get; }
        public JsonNode? Metrics { get; }
        public JsonNode? State { get; }
        public JsonNode? Lineage { get; }
        public bool HasSnapshot => Ctx.Snapshot is not null;
        public RequirementsDoc? Requirements { get; }
        public ImpactReport? Impact { get; }
        public ReproReport? Repro { get; }
        public DesignDoc? Design { get; }
        public TestPlan? Plan { get; }
        public IntegrationReport? Integration { get; }
        public TestReport? Tests { get; }
        public Traceability? Trace { get; }
        public SecurityReport? Security { get; }
        public DocsReport? Docs { get; }
        public ReleaseReadiness? Release { get; }
        public List<string> AvailableArtifacts { get; }
        public List<NodeView> Nodes { get; }

        public Evidence(AgentContext ctx, JsonNode? metrics, JsonNode? state, JsonNode? lineage)
        {
            Ctx = ctx; Metrics = metrics; State = state; Lineage = lineage;
            Requirements = ctx.ReadOrDefault<RequirementsDoc>("requirements");
            Impact = ctx.ReadOrDefault<ImpactReport>("impact");
            Repro = ctx.ReadOrDefault<ReproReport>("repro");
            Design = ctx.ReadOrDefault<DesignDoc>("design");
            Plan = ctx.ReadOrDefault<TestPlan>("test-plan");
            Integration = ctx.ReadOrDefault<IntegrationReport>("integration");
            Tests = ctx.ReadOrDefault<TestReport>("test-report");
            Trace = ctx.ReadOrDefault<Traceability>("traceability");
            Security = ctx.ReadOrDefault<SecurityReport>("security-report");
            Docs = ctx.ReadOrDefault<DocsReport>("docs");
            Release = ctx.ReadOrDefault<ReleaseReadiness>("release-readiness");
            AvailableArtifacts = KnownArtifacts.Where(ctx.Has).ToList();
            Nodes = ReadNodes(state);
        }

        public string FeatureTitle(string id) => Ctx.Catalog.Features.TryGetValue(id, out var f) ? f.Title : id;
    }

    private static List<NodeView> ReadNodes(JsonNode? state)
    {
        var list = new List<NodeView>();
        foreach (var (key, value) in JsonView.Entries(JsonView.Prop(state, "nodes", "nodeStates")))
        {
            var id = value is JsonObject ? JsonView.Str(value, "id", "nodeId") : "";
            if (id.Length == 0) id = key ?? "";
            if (id.Length == 0) continue;
            var status = value is JsonValue ? JsonView.Text(value) : JsonView.Str(value, "status", "state");
            var deps = JsonView.Items(JsonView.Deep(value, "deps", "dependsOn", "dependencies")).Select(JsonView.Text).Where(d => d.Length > 0).ToList();
            list.Add(new NodeView(id, status.ToLowerInvariant(), JsonView.Str(value, "stage") is { Length: > 0 } st ? st : JsonView.Text(JsonView.Deep(value, "stage")),
                JsonView.Str(value, "agent") is { Length: > 0 } ag ? ag : JsonView.Text(JsonView.Deep(value, "agent")), deps, value));
        }
        return list;
    }

    // ───────────── 1. Outcome ─────────────

    private static void Outcome(StringBuilder sb, Evidence s)
    {
        var ctx = s.Ctx;
        var decision = s.Release?.Decision ?? "NOT ISSUED (the run ended before the release gate)";
        var runStatus = JsonView.Str(s.State, "status", "runStatus");
        var features = s.Integration?.InstalledFeatures ?? s.Design?.Features ?? new List<string>();
        var changes = ExecutionSupport.NetChanges(ExecutionSupport.ReadChanges(s.State));

        sb.AppendLine("## 1. Outcome").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Item", "Value" }, new[]
        {
            new[] { "Scenario", $"{ctx.Scenario.Title} (`{ctx.Scenario.Id}`, {ctx.Scenario.Mode})" },
            new[] { "Run status", runStatus.Length > 0 ? runStatus : "n/a" },
            new[] { "Release decision", $"**{decision}**" },
            new[] { "Features", features.Count == 0 ? "none" : string.Join(", ", features.Select(f => $"`{f}`")) },
            new[] { "Files", $"{changes.Created.Count} created, {changes.Modified.Count} modified" },
            new[] { "Tests", s.Tests is null ? "not run" : $"{s.Tests.Passed} passed / {s.Tests.Total} total" },
            new[] { "Acceptance coverage", s.Trace is null ? "not computed" : ExecutionSupport.Percent(s.Trace.Coverage) },
            new[] { "Security scan", s.Security is null ? "not run" : s.Security.Passed ? $"passed ({s.Security.Findings.Count} finding(s))" : "FAILED" },
        })).AppendLine();

        var para = new StringBuilder();
        para.Append($"The orchestrator processed the {ctx.Scenario.Mode} scenario \"{ctx.Scenario.Title}\"");
        para.Append(features.Count > 0 ? $" and assembled {features.Count} feature(s) ({string.Join(", ", features.Select(s.FeatureTitle))})" : " but no feature was assembled");
        if (changes.Created.Count + changes.Modified.Count > 0) para.Append($", changing {changes.Created.Count + changes.Modified.Count} file(s) in the generated workspace");
        para.Append('.');
        if (s.Tests is not null) para.Append(s.Tests.Green ? $" The full suite is green ({s.Tests.Passed}/{s.Tests.Total})." : $" The full suite is RED ({s.Tests.Failed} failed of {s.Tests.Total}).");
        if (s.Repro is not null) para.Append(' ').Append(s.Repro.Summary).Append('.');
        para.Append(s.Release is null
            ? " The run ended before a release decision was issued; see the orchestration evidence below for where and why."
            : $" The release manager issued {s.Release.Decision} ({s.Release.Checklist.Count(c => c.Status == "pass")} of {s.Release.Checklist.Count} checklist items pass, {s.Release.Checklist.Count(c => c.Status == "warn")} warning(s), {s.Release.Checklist.Count(c => c.Status == "fail")} failure(s)).");
        sb.AppendLine(para.ToString()).AppendLine();
    }

    // ───────────── 2. Plan and rationale ─────────────

    private static void Plan(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 2. Plan and rationale").AppendLine();
        sb.AppendLine("### Normalised problem statement").AppendLine();
        sb.AppendLine(s.Requirements is null ? "_Requirements were not produced in this run._" : "> " + string.Join(" ", s.Requirements.ProblemStatement.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).AppendLine();

        if (s.Requirements is not null)
        {
            sb.AppendLine("### Features chosen and why").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Feature", "Title", "Source", "Why" },
                s.Requirements.Features.Select(f => new[] { $"`{f.Id}`", f.Title, f.Source, f.Rationale }))).AppendLine();
        }

        sb.AppendLine("### Task decomposition").AppendLine();
        if (s.Design is null) sb.AppendLine("_No design was produced in this run._").AppendLine();
        else
            sb.Append(ExecutionSupport.Table(new[] { "Task", "Feature", "Kind", "Depends on", "Modifies existing", "Sensitive" },
                s.Design.Tasks.Select(t => new[]
                {
                    $"`{t.Id}`", $"`{t.Feature}`", t.Kind, t.DependsOn.Count == 0 ? "-" : string.Join(", ", t.DependsOn.Select(d => $"`{d}`")),
                    t.ModifiesExisting.Count == 0 ? "-" : string.Join(", ", t.ModifiesExisting.Select(m => $"`{m}`")),
                    t.Sensitive ? $"yes: {t.SensitiveReason}" : "no",
                }))).AppendLine();

        if (s.Design is { Adrs.Count: > 0 })
        {
            sb.AppendLine("### Key architecture decisions").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "ADR", "Title", "Decision", "Consequences" }, s.Design.Adrs.Select(a => new[] { a.Id, a.Title, a.Decision, a.Consequences }))).AppendLine();
        }

        sb.AppendLine("### Executed node graph").AppendLine();
        sb.AppendLine(Mermaid(s.Nodes, JsonView.Str(s.State, "mermaid")));
    }

    /// <summary>
    /// Flowchart of the executed graph. The engine's own graph rendering (edges + status labels, published in State().mermaid) is used when present;
    /// otherwise the graph is rebuilt from node ids, dependencies and statuses. Node colours are always derived from the node statuses in State().
    /// </summary>
    private static string Mermaid(List<NodeView> nodes, string engineGraph)
    {
        if (nodes.Count == 0 && engineGraph.Length == 0) return "_The run snapshot did not include node states, so no graph can be drawn._\n";
        static string Id(string raw) => new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        if (engineGraph.TrimStart().StartsWith("flowchart", StringComparison.Ordinal))
        {
            foreach (var line in engineGraph.Replace("```mermaid", "").Replace("```", "").Split('\n')) if (line.Trim().Length > 0) sb.AppendLine(line.TrimEnd());
            nodes = nodes.Where(n => engineGraph.Contains($"{Id(n.Id)}[")).ToList();
        }
        else
        {
            sb.AppendLine("flowchart LR");
            foreach (var n in nodes)
                sb.AppendLine($"    {Id(n.Id)}[\"{n.Id.Replace("\"", "'")}<br/>{(n.Status.Length > 0 ? n.Status : "unknown")}\"]");
            foreach (var n in nodes)
                foreach (var d in n.Deps.Where(ids.Contains))
                    sb.AppendLine($"    {Id(d)} --> {Id(n.Id)}");
        }
        sb.AppendLine("    classDef done fill:#d9f2e0,stroke:#2e7d32,color:#12351a");
        sb.AppendLine("    classDef failed fill:#fbd9d9,stroke:#c62828,color:#4a1111");
        sb.AppendLine("    classDef skipped fill:#eceff1,stroke:#90a4ae,color:#37474f");
        sb.AppendLine("    classDef other fill:#fff3cd,stroke:#f9a825,color:#4a3b00");
        foreach (var g in nodes.GroupBy(n => n.Status switch
        {
            "done" => "done",
            "failed" or "rejected" or "blocked" => "failed",
            "skipped" or "cancelled" => "skipped",
            _ => "other",
        }))
            sb.AppendLine($"    class {string.Join(",", g.Select(n => Id(n.Id)))} {g.Key}");
        sb.AppendLine("```");
        return sb.ToString();
    }

    // ───────────── 3. Artifacts ─────────────

    private static void Artifacts(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 3. Artifacts").AppendLine();
        var rows = new List<string[]>();
        var metaSource = JsonView.Deep(s.Lineage, "artifacts") ?? JsonView.Deep(s.State, "artifacts");
        foreach (var item in JsonView.Items(metaSource).Where(i => i is JsonObject))
        {
            var name = JsonView.Str(item, "name", "artifact");
            if (name.Length == 0) continue;
            var hash = JsonView.Str(item, "hash", "sha256");
            rows.Add(new[] { $"`{name}`", JsonView.Str(item, "version") is { Length: > 0 } v ? v : "-", hash.Length > 0 ? $"`{hash[..Math.Min(12, hash.Length)]}`" : "-" });
        }
        if (rows.Count == 0)
            foreach (var name in s.AvailableArtifacts)
                rows.Add(new[] { $"`{name}`", "latest", $"`{Json.Hash(s.Ctx.ArtifactReader(name))[..12]}`" });
        sb.AppendLine("### Run artifacts").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Artifact", "Version", "Hash (prefix)" }, rows.OrderBy(r => r[0], StringComparer.Ordinal))).AppendLine();

        var changes = ExecutionSupport.NetChanges(ExecutionSupport.ReadChanges(s.State));
        sb.AppendLine("### Generated workspace files").AppendLine();
        sb.AppendLine($"**Created ({changes.Created.Count})**").AppendLine();
        sb.Append(FileGroups(changes.Created.Select(c => c.Path))).AppendLine();
        sb.AppendLine($"**Modified ({changes.Modified.Count})**").AppendLine();
        sb.Append(FileGroups(changes.Modified.Select(c => c.Path))).AppendLine();
        if (changes.Deleted.Count > 0)
        {
            sb.AppendLine($"**Deleted ({changes.Deleted.Count})**").AppendLine();
            sb.Append(FileGroups(changes.Deleted.Select(c => c.Path))).AppendLine();
        }
    }

    private static string FileGroups(IEnumerable<string> paths) =>
        ExecutionSupport.Table(new[] { "Directory", "Files" },
            paths.GroupBy(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : ".").OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new[] { $"`{g.Key}`", string.Join(", ", g.Select(p => $"`{p[(p.LastIndexOf('/') + 1)..]}`")) }));

    // ───────────── 4. Risks, trade-offs and validation ─────────────

    private static void Risks(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 4. Risks, trade-offs and validation").AppendLine();
        var covered = (s.Trace?.Matrix ?? new()).Where(r => r.Covered).Select(r => r.AcceptanceId).ToHashSet();

        sb.AppendLine("### Risk register").AppendLine();
        if (s.Design is null) sb.AppendLine("_No design (and therefore no risk register) was produced in this run._").AppendLine();
        else
            sb.Append(ExecutionSupport.Table(new[] { "Risk", "Scenario", "L / I", "Mitigation", "Validated by", "Residual" },
                s.Design.Risks.Select(r => new[]
                {
                    r.Id, r.Scenario, $"{r.Likelihood} / {r.Impact}", r.Mitigation,
                    r.Validation.Count == 0 ? "-" : string.Join(", ", r.Validation.Select(v => $"{v} {(covered.Contains(v) ? "[covered]" : "[NOT covered]")}")),
                    r.Residual,
                }))).AppendLine();

        sb.AppendLine("### Verification results").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Check", "Result" }, new[]
        {
            new[] { "Test totals", s.Tests is null ? "not run" : $"{s.Tests.Passed} passed, {s.Tests.Failed} failed, {s.Tests.Skipped} skipped, {s.Tests.Total} total in {s.Tests.DurationMs} ms" + (s.Tests.Failures.Count > 0 ? $"; failing: {string.Join(", ", s.Tests.Failures.Take(5))}" : "") },
            new[] { "Traceability coverage", s.Trace is null ? "not computed" : $"{ExecutionSupport.Percent(s.Trace.Coverage)} ({s.Trace.Matrix.Count(r => r.Covered)}/{s.Trace.Matrix.Count}){(s.Trace.Missing.Count > 0 ? "; missing: " + string.Join(", ", s.Trace.Missing) : "")}" },
            new[] { "Security and compliance scan", s.Security is null ? "not run" : $"{(s.Security.Passed ? "passed" : "FAILED")}: {s.Security.ScannedFiles} file(s) scanned, {s.Security.Findings.Count} finding(s)" + (s.Security.Findings.Count > 0 ? " (" + string.Join(", ", s.Security.Findings.GroupBy(f => $"{f.Rule}/{f.Severity}").Select(g => $"{g.Key} x{g.Count()}")) + ")" : "") },
            new[] { "Defect reproduction", s.Repro is null ? "not applicable" : s.Repro.Summary },
        })).AppendLine();

        if (s.Trace is { Matrix.Count: > 0 })
        {
            sb.AppendLine("### Traceability matrix").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Acceptance criterion", "Feature", "Tests", "Covered" },
                s.Trace.Matrix.Select(r => new[] { r.AcceptanceId, $"`{r.Feature}`", r.Tests.Count == 0 ? "-" : string.Join("; ", r.Tests.Select(t => $"`{t[(t.LastIndexOf('/') + 1)..]}`")), r.Covered ? "yes" : "NO" }))).AppendLine();
        }

        sb.AppendLine("### Release checklist").AppendLine();
        if (s.Release is null) sb.AppendLine("_The release gate was not reached._").AppendLine();
        else
        {
            sb.Append(ExecutionSupport.Table(new[] { "Id", "Check", "Status", "Evidence" }, s.Release.Checklist.Select(c => new[] { c.Id, c.Title, c.Status.ToUpperInvariant(), c.Evidence }))).AppendLine();
            sb.AppendLine("### Accepted residual risks").AppendLine();
            sb.Append(ExecutionSupport.Bullets(s.Release.ResidualRisks, "_None accepted._")).AppendLine();
        }
    }

    // ───────────── 5. Assumptions ─────────────

    private static void Assumptions(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 5. Assumptions").AppendLine();
        sb.AppendLine("### Requirement assumptions").AppendLine();
        sb.Append(ExecutionSupport.Bullets(s.Requirements?.Assumptions ?? new(), "_No assumptions recorded._")).AppendLine();

        sb.AppendLine("### Ambiguities and resolutions").AppendLine();
        var ambiguities = s.Requirements?.Ambiguities ?? new();
        sb.Append(ExecutionSupport.Table(new[] { "Id", "Term", "Severity", "Question", "Assumed default", "Resolved", "Resolution" },
            ambiguities.Select(a => new[] { a.Id, a.Term, a.Severity, a.Question, a.AssumedDefault, a.Resolved ? "yes" : "no", a.Resolution ?? "-" }))).AppendLine();
        if (s.Requirements is { Answers.Count: > 0 })
        {
            sb.AppendLine("**Human answers**").AppendLine();
            sb.Append(ExecutionSupport.Bullets(s.Requirements.Answers.Select(a => $"`{a.Key}`: {a.Value}"))).AppendLine();
        }

        sb.AppendLine("### Human approvals and clarifications").AppendLine();
        var records = JsonView.Items(JsonView.Prop(s.State, "approvals")).ToList();
        sb.Append(ExecutionSupport.Table(new[] { "Node", "Kind", "When", "Risk", "Reason", "Decision", "Approver", "Comment" },
            records.Select(r => new[]
            {
                $"`{JsonView.Str(r, "nodeId", "node")}`", JsonView.Str(r, "kind"), JsonView.Str(r, "when"), JsonView.Str(r, "risk"), JsonView.Str(r, "reason"),
                JsonView.Str(r, "decision", "status"), JsonView.Str(r, "approver", "by"), JsonView.Str(r, "comment"),
            }))).AppendLine();
    }

    // ───────────── 6. Orchestration evidence ─────────────

    private static void Orchestration(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 6. Orchestration evidence").AppendLine();
        var m = s.Metrics;
        if (m is null) sb.AppendLine("_The run snapshot provided no metrics._").AppendLine();
        else
        {
            sb.Append(ExecutionSupport.Table(new[] { "Metric", "Value" }, new[]
            {
                new[] { "Success rate (nodes)", Fmt(m, "pct", "successRate") + NodesSuffix(m) },
                new[] { "Attempt success rate", Fmt(m, "pct", "attemptSuccessRate") + (JsonView.Prop(m, "totalAttempts") is { } ta ? $" ({JsonView.Text(ta)} attempts)" : "") },
                new[] { "Retries", Fmt(m, "int", "retries") },
                new[] { "Rollbacks", Fmt(m, "int", "rollbacks") },
                new[] { "Fallbacks", Fmt(m, "int", "fallbacks") },
                new[] { "Replans", Fmt(m, "int", "replans") },
                new[] { "Policy violations", Fmt(m, "int", "policyViolations") },
                new[] { "MTTR", JsonView.Prop(m, "mttrMs", "mttr") is null ? "n/a (no failure had to be recovered)" : Fmt(m, "ms", "mttrMs", "mttr") },
                new[] { "End-to-end latency", Fmt(m, "ms", "endToEndMs", "endToEndLatencyMs") },
                new[] { "Critical path", CriticalPath(m) },
                new[] { "Approvals requested / granted / rejected", $"{Fmt(m, "int", "approvalsRequested")} / {Fmt(m, "int", "approvalsGranted")} / {Fmt(m, "int", "approvalsRejected")}" },
            })).AppendLine();
        }

        sb.AppendLine("### What actually happened").AppendLine();
        var nodes = s.Nodes;
        if (nodes.Count == 0) sb.AppendLine("_The run snapshot did not include node states; per-node retry and rollback details are unavailable._").AppendLine();
        else
        {
            var retried = nodes.Where(n => Count(n.Raw, "retries") > 0).ToList();
            var rolled = nodes.Where(n => Count(n.Raw, "rollbacks") > 0).ToList();
            var fallbacks = nodes.Where(n => JsonView.Prop(n.Raw, "usedFallback") is { } f && JsonView.Text(f) == "true").ToList();
            var failed = nodes.Where(n => n.Status is "failed" or "rejected" or "blocked").ToList();
            sb.AppendLine("**Retries**").AppendLine();
            sb.Append(ExecutionSupport.Bullets(retried.Select(n => $"`{n.Id}`: {Count(n.Raw, "retries")} retr{(Count(n.Raw, "retries") == 1 ? "y" : "ies")} ({JsonView.Str(n.Raw, "attempts")} attempt(s)){ErrorSuffix(n)}"), "_None._")).AppendLine();
            sb.AppendLine("**Rollbacks**").AppendLine();
            sb.Append(ExecutionSupport.Bullets(rolled.Select(n => $"`{n.Id}`: {Count(n.Raw, "rollbacks")} rollback(s) of the node's file changes"), "_None._")).AppendLine();
            sb.AppendLine("**Fallbacks**").AppendLine();
            sb.Append(ExecutionSupport.Bullets(fallbacks.Select(n => $"`{n.Id}`: fallback agent used after the primary agent's retries were exhausted"), "_None._")).AppendLine();
            if (failed.Count > 0)
            {
                sb.AppendLine("**Nodes that did not complete**").AppendLine();
                sb.Append(ExecutionSupport.Bullets(failed.Select(n => $"`{n.Id}`: {n.Status}{ErrorSuffix(n)}"))).AppendLine();
            }
        }

        sb.AppendLine("**Replans**").AppendLine();
        sb.Append(ExecutionSupport.Bullets(JsonView.Items(JsonView.Prop(s.State, "replans")).Select(r =>
            $"{JsonView.Str(r, "reason")}" + (JsonView.Prop(r, "staleNodes") is { } stale && JsonView.Text(stale).Length > 0 ? $" (invalidated: {JsonView.Text(stale)})" : "")), "_None._")).AppendLine();

        sb.AppendLine("**Policy violations**").AppendLine();
        sb.Append(ExecutionSupport.Bullets(JsonView.Items(JsonView.Prop(s.State, "policyViolations", "violations")).Select(v =>
            $"{JsonView.Str(v, "rule")} [{JsonView.Str(v, "severity")}] at `{JsonView.Str(v, "nodeId", "node")}`: {JsonView.Str(v, "message")}" + (JsonView.Str(v, "file") is { Length: > 0 } f ? $" ({f})" : "")), "_None._")).AppendLine();
    }

    private static string ErrorSuffix(NodeView n) => JsonView.Str(n.Raw, "error") is { Length: > 0 } e ? $" - last error: {e}" : "";

    private static double Count(JsonNode raw, string key) => JsonView.Num(JsonView.Prop(raw, key)) ?? 0;

    private static string NodesSuffix(JsonNode m) =>
        JsonView.Prop(m, "nodesDone") is { } d && JsonView.Prop(m, "nodesTotal") is { } t ? $" ({JsonView.Text(d)}/{JsonView.Text(t)} nodes done)" : "";

    private static string CriticalPath(JsonNode m)
    {
        var path = JsonView.Items(JsonView.Prop(m, "criticalPath")).Select(JsonView.Text).ToList();
        if (path.Count == 0) return "n/a";
        return string.Join(" -> ", path.Select(p => $"`{p}`")) + (JsonView.Prop(m, "criticalPathMs") is not null ? $" ({Fmt(m, "ms", "criticalPathMs")})" : "");
    }

    private static string Fmt(JsonNode m, string kind, params string[] names)
    {
        var node = JsonView.Prop(m, names);
        var n = JsonView.Num(node);
        if (node is null) return "n/a";
        if (n is null) return JsonView.Text(node);
        return kind switch
        {
            "pct" => ExecutionSupport.Percent(n.Value),
            "ms" => n.Value >= 1000 ? ExecutionSupport.Inv(n.Value / 1000, "0.00") + " s" : ExecutionSupport.Inv(n.Value, "0.#") + " ms",
            _ => ExecutionSupport.Inv(n.Value, "0.##"),
        };
    }

    // ───────────── 7. Decision lineage ─────────────

    private sealed record DecisionView(string Node, string Title, string Rationale, string Tradeoff, string Alternatives);

    private static readonly HashSet<string> ContainerKeys = new(StringComparer.OrdinalIgnoreCase) { "decisions", "nodes", "lineage", "entries", "items", "records", "artifacts", "events", "basedOn" };

    private static void CollectDecisions(JsonNode? n, string? node, List<DecisionView> acc)
    {
        switch (n)
        {
            case JsonObject o:
                var here = JsonView.Str(o, "nodeId", "node") is { Length: > 0 } id ? id : node;
                if (JsonView.Prop(o, "title") is not null && JsonView.Prop(o, "rationale") is not null)
                {
                    acc.Add(new DecisionView(here ?? "(unattributed)", JsonView.Str(o, "title"), JsonView.Str(o, "rationale"), JsonView.Str(o, "tradeoff"), JsonView.Str(o, "alternatives")));
                    return;
                }
                foreach (var kv in o)
                {
                    var ctxNode = here ?? (kv.Value is JsonArray or JsonObject && !ContainerKeys.Contains(kv.Key) ? kv.Key : null);
                    CollectDecisions(kv.Value, ctxNode, acc);
                }
                break;
            case JsonArray a:
                foreach (var item in a) CollectDecisions(item, node, acc);
                break;
        }
    }

    private static void Lineage(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 7. Decision lineage").AppendLine();
        var decisions = new List<DecisionView>();
        CollectDecisions(s.Lineage, null, decisions);
        if (decisions.Count == 0) { sb.AppendLine("_The run snapshot recorded no decisions._").AppendLine(); return; }
        sb.AppendLine($"{decisions.Count} decision(s) recorded by {decisions.Select(d => d.Node).Distinct().Count()} node(s). Each links its rationale and the trade-off accepted.").AppendLine();
        foreach (var group in decisions.GroupBy(d => d.Node))
        {
            sb.AppendLine($"### `{group.Key}`").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Decision", "Rationale", "Trade-off" }, group.Select(d => new[]
            {
                d.Title + (d.Alternatives.Length > 0 ? $" (alternatives: {d.Alternatives})" : ""), d.Rationale, d.Tradeoff.Length > 0 ? d.Tradeoff : "-",
            }))).AppendLine();
        }
    }

    // ───────────── 8. Limitations ─────────────

    private static void Limitations(StringBuilder sb, Evidence s)
    {
        sb.AppendLine("## 8. Limitations").AppendLine();
        var items = new List<string>
        {
            "**Deterministic, rule-based agents instead of LLMs.** Planning, implementation, review and documentation are rule-driven and assemble code from a vetted component library, so runs are reproducible offline, but the system cannot invent features outside the library or interpret truly novel requirements.",
            "**In-memory link store.** The generated service keeps links, analytics and idempotency records in process memory; everything is lost on restart and nothing is shared between instances.",
            "**No authentication or authorisation.** Anyone who can reach the service can create, inspect and delete links (documented as risk R-UNAUTH-DELETE); deploy it behind an authenticated gateway.",
            "**Single-instance rate limiting and idempotency.** Counters and idempotency keys live in one process; a horizontally scaled deployment would need a shared store.",
            "**Scripted approver in demos.** Human approvals and clarifications are answered by the scenario's scripted approver, so the human-in-the-loop path is exercised but not by a real person.",
            "**Static-analysis based impact analysis.** The brownfield impact report reasons over syntax (usings, types, routes, hooks) and is not a data-flow or runtime analysis; behavioural coupling it cannot see is only caught by the tests.",
            "**.NET build and test time.** Each full verification runs a real `dotnet build` + `dotnet test`" + (s.Tests is not null ? $" (this run: {s.Tests.DurationMs} ms)" : "") + ", which dominates end-to-end latency and is the main limit on how often the pipeline can retry.",
        };
        if (s.Ctx.Scenario.Mode == "brownfield") items.Add("**Baseline fidelity.** The brownfield baseline is a seeded v1 of the service with known defects, not a customer repository; real codebases will exercise the analyst far harder.");
        if (!s.HasSnapshot) items.Add("The run snapshot was unavailable, so metrics, node states and lineage sections are empty.");
        sb.Append(ExecutionSupport.Bullets(items));
    }
}

