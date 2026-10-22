namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Final release gate: evaluates the evidence produced by the run against an explicit checklist and issues GO / NO-GO with a rollback plan.
/// Read-only. GO iff no checklist item fails (warnings are allowed and surfaced).
/// </summary>
public sealed class ReleaseManagerAgent : IAgent
{
    public const string AgentName = "release-manager";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => new(Array.Empty<string>(), ReadsRunSnapshot: true);

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;

        var requirements = ctx.ReadOrDefault<RequirementsDoc>("requirements");
        var design = ctx.ReadOrDefault<DesignDoc>("design");
        var tests = ctx.ReadOrDefault<TestReport>("test-report");
        var trace = ctx.ReadOrDefault<Traceability>("traceability");
        var security = ctx.ReadOrDefault<SecurityReport>("security-report");
        var docs = ctx.ReadOrDefault<DocsReport>("docs");
        var repro = ctx.ReadOrDefault<ReproReport>("repro");

        var changes = ExecutionSupport.NetChanges(ExecutionSupport.ReadChanges(ctx.Snapshot?.State()));
        var changedPaths = changes.Created.Count + changes.Modified.Count + changes.Deleted.Count;

        var checklist = new List<ChecklistItem>
        {
            Item("REL-01", "All tests green", tests is null ? ("fail", "No test report is available.")
                : tests.Green ? ("pass", $"{tests.Passed} passed / {tests.Total} total")
                : ("fail", $"{tests.Passed} passed / {tests.Total} total; {tests.Failed} failed, exit code {tests.ExitCode}{(tests.Failures.Count > 0 ? ": " + string.Join(", ", tests.Failures.Take(5)) : "")}")),

            Item("REL-02", "Acceptance-criteria coverage is 100%", trace is null ? ("fail", "No traceability matrix is available.")
                : trace.Coverage == 1.0 ? ("pass", $"{trace.Matrix.Count}/{trace.Matrix.Count} acceptance criteria traced to tests")
                : ("fail", $"{trace.Matrix.Count - trace.Missing.Count}/{trace.Matrix.Count} covered ({ExecutionSupport.Percent(trace.Coverage)}); missing: {string.Join(", ", trace.Missing)}")),

            Item("REL-03", "Security and compliance scan has no critical/block findings", SecurityStatus(security)),

            Item("REL-04", "No unresolved high-severity ambiguities", requirements is null ? ("fail", "No requirements artifact is available.")
                : Unresolved(requirements) is { Count: > 0 } open ? ("fail", $"Unresolved high-severity ambiguity: {string.Join(", ", open)}")
                : ("pass", $"{requirements.Ambiguities.Count} ambiguity(ies) total, none unresolved at high severity")),

            Item("REL-05", "Documentation generated", DocsStatus(docs)),
        };

        if (repro is not null)
            checklist.Add(Item("REL-06", "Defect reproduced before the fix and tests green after",
                repro.Reproduced && tests is { Green: true } ? ("pass", $"{repro.FailingTests}/{repro.TotalTests} hardening test(s) failed before the fix; suite is green after ({tests.Passed}/{tests.Total})")
                : !repro.Reproduced ? ("fail", "The defect was not reproduced on the baseline, so the fix is not proven: " + repro.Summary)
                : ("fail", "The defect was reproduced but the suite is not green after the fix.")));

        checklist.Add(Item("REL-07", $"Change blast radius <= {ChangeControl.MaxFilesChangedPerRun} files",
            changedPaths <= ChangeControl.MaxFilesChangedPerRun
                ? ("pass", $"{changedPaths} file(s) changed ({changes.Created.Count} created, {changes.Modified.Count} modified, {changes.Deleted.Count} deleted)")
                : ("fail", $"{changedPaths} file(s) changed, limit is {ChangeControl.MaxFilesChangedPerRun}")));

        checklist.Add(Item("REL-08", "Every risk with validation criteria is covered by tests", RiskStatus(design, trace)));

        var decision = checklist.Any(c => c.Status == "fail") ? "NO-GO" : "GO";
        var rollback = BuildRollbackPlan(changes);
        var residual = (design?.Risks ?? new()).Where(r => r.Residual.StartsWith("Accepted", StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Id}: {r.Scenario} - {r.Residual}").ToList();

        var report = new ReleaseReadiness(decision, checklist, changes.Created.Count, changes.Modified.Count, rollback, residual);
        var failed = checklist.Where(c => c.Status == "fail").ToList();
        var warned = checklist.Where(c => c.Status == "warn").ToList();
        ctx.Log($"release decision: {decision} ({checklist.Count(c => c.Status == "pass")} pass, {warned.Count} warn, {failed.Count} fail)");

        return new AgentResult().Add("release-readiness", report)
            .Decide($"Release decision: {decision}",
                (decision == "GO"
                    ? $"All {checklist.Count} checklist items pass or warn ({checklist.Count(c => c.Status == "pass")} pass, {warned.Count} warn). "
                    : $"{failed.Count} checklist item(s) failed: {string.Join("; ", failed.Select(c => $"{c.Id} {c.Title} - {c.Evidence}"))}. ")
                + (warned.Count > 0 ? $"Warnings: {string.Join("; ", warned.Select(c => $"{c.Id} - {c.Evidence}"))}. " : "")
                + $"Residual accepted risks: {residual.Count}. Rollback plan has {rollback.Count} step(s).",
                new[] { decision == "GO" ? "NO-GO (not justified by the evidence)" : "GO with waivers (not permitted: failed items are hard gates)" },
                "A strict all-pass gate trades speed for safety; warnings are carried as documented residual risk instead of blocking.");
    }

    private static ChecklistItem Item(string id, string title, (string Status, string Evidence) result) => new(id, title, result.Status, result.Evidence);

    private static (string, string) SecurityStatus(SecurityReport? s)
    {
        if (s is null) return ("fail", "No security report is available.");
        var blocking = s.Findings.Where(f => f.Severity is "critical" or "block").ToList();
        if (blocking.Count > 0 || !s.Passed)
            return ("fail", $"{blocking.Count} critical/block finding(s): {string.Join(", ", blocking.Take(5).Select(f => $"{f.Rule} {f.File}:{f.Line}"))}");
        var warns = s.Findings.Count(f => f.Severity == "warn");
        return warns > 0 ? ("warn", $"{s.ScannedFiles} file(s) scanned; no blocking findings, {warns} warning(s)")
                         : ("pass", $"{s.ScannedFiles} file(s) scanned; no findings");
    }

    private static List<string> Unresolved(RequirementsDoc r) =>
        r.Ambiguities.Where(a => a.Severity.Equals("high", StringComparison.OrdinalIgnoreCase) && !a.Resolved).Select(a => $"{a.Id} ({a.Term})").ToList();

    private static (string, string) DocsStatus(DocsReport? docs)
    {
        if (docs is null) return ("fail", "No documentation report is available.");
        var have = docs.Files.Select(f => f.Path.Replace('\\', '/')).ToHashSet();
        var missing = new[] { "README.md", "docs/API.md", "docs/ARCHITECTURE.md" }.Where(p => !have.Contains(p)).ToList();
        return missing.Count == 0 ? ("pass", $"{docs.Files.Count} document(s) generated incl. README.md, docs/API.md, docs/ARCHITECTURE.md")
                                  : ("fail", $"Missing: {string.Join(", ", missing)}");
    }

    private static (string, string) RiskStatus(DesignDoc? design, Traceability? trace)
    {
        if (design is null) return ("fail", "No design artifact is available.");
        var validated = design.Risks.Where(r => r.Validation.Count > 0).ToList();
        var covered = (trace?.Matrix ?? new()).Where(r => r.Covered).Select(r => r.AcceptanceId).ToHashSet();
        var exceptions = validated.Select(r => (Risk: r, Gaps: r.Validation.Where(v => !covered.Contains(v)).ToList())).Where(x => x.Gaps.Count > 0).ToList();
        return exceptions.Count == 0
            ? ("pass", $"{validated.Count} validated risk(s); all their acceptance criteria are covered ({design.Risks.Count - validated.Count} risk(s) have no validation criteria)")
            : ("fail", string.Join("; ", exceptions.Select(x => $"{x.Risk.Id} not covered: {string.Join(", ", x.Gaps)}")));
    }

    private static List<string> BuildRollbackPlan(
        (List<(string Path, string NodeId)> Created, List<(string Path, string NodeId)> Modified, List<(string Path, string NodeId)> Deleted) changes)
    {
        var plan = new List<string>();
        plan.AddRange(changes.Modified.Select(m => $"Restore pre-change version of {m.Path} (journal revert of node {m.NodeId})"));
        plan.AddRange(changes.Deleted.Select(d => $"Restore deleted file {d.Path} (journal revert of node {d.NodeId})"));
        plan.AddRange(changes.Created.Select(c => $"Delete {c.Path} (revert node {c.NodeId})"));
        plan.Add("Redeploy previous build and keep the baseline snapshot as rollback target");
        return plan;
    }
}
