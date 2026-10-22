namespace Agentic.Orchestrator.Agents;

/// <summary>Runs the static policy scan (<see cref="PolicyEngine"/>) over the generated workspace. Read-only; the exit gate decides what the findings mean.</summary>
public sealed class SecurityReviewerAgent : IAgent
{
    public const string AgentName = "security-reviewer";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => new(Array.Empty<string>());

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var report = PolicyEngine.ScanWorkspace(ctx.Workspace);
        var result = new AgentResult().Add("security-report", report);

        foreach (var group in report.Findings.GroupBy(f => f.Rule).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rule = PolicyEngine.Rules.FirstOrDefault(r => r.Id == group.Key);
            var severity = group.First().Severity;
            var files = group.Select(f => f.File).Distinct().ToList();
            var examples = string.Join("; ", group.Take(3).Select(f => $"{f.File}:{f.Line}"));
            result.Decide($"{group.Key} [{severity}]: {group.Count()} hit(s) in {files.Count} file(s)",
                $"{rule?.Description ?? group.First().Message} – e.g. {examples}{(group.Count() > 3 ? ", …" : "")}.",
                new[] { severity == "warn" ? "Block the release (disproportionate for a warning)" : "Waive the rule (not permitted without a policy change)" },
                severity switch
                {
                    "critical" => "Critical findings trigger an immediate safe-stop; no retry is attempted.",
                    "block" => "Block findings fail the exit gate and force a fix before release.",
                    _ => "Warnings are recorded and surfaced in the release checklist but do not block.",
                });
        }

        if (report.Findings.Count == 0)
            result.Decide("No policy findings", $"Scanned {report.ScannedFiles} file(s) against {PolicyEngine.Rules.Count} rules (secrets, process execution, outbound calls, weak randomness, privacy, code quality); nothing matched.",
                new[] { "Skip the scan for unchanged files (would miss regressions in shared code)" }, "A full-workspace scan is cheap and keeps the evidence complete.");

        ctx.Log($"security scan: {report.ScannedFiles} file(s), {report.Findings.Count} finding(s), passed={report.Passed}");
        return result;
    }
}
