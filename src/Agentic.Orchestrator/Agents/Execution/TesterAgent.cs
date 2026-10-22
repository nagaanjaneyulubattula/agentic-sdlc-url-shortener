using System.Text.RegularExpressions;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Verification agent. Modes (param "mode"): full (run the generated test suite), repro (reproduce a baseline defect with the hardening
/// tests on a throw-away copy) and traceability (map acceptance criteria to xUnit tests via [Trait("AC","...")]).
/// It never fails because tests are red – an exit gate decides what a red report means.
/// </summary>
public sealed class TesterAgent : IAgent
{
    public const string AgentName = "tester";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => new(Array.Empty<string>(), CanRunTests: true);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    private static readonly Regex TraitRx = new(@"\[Trait\(\s*""AC""\s*,\s*""(AC-[A-Z]+-\d+)""\s*\)\]", RegexOptions.Compiled);
    private static readonly Regex MethodRx = new(@"public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ClassRx = new(@"\bclass\s+\w+", RegexOptions.Compiled);

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mode = ctx.Param("mode", "full");
        return mode switch
        {
            "full" => await FullAsync(ctx, ct),
            "repro" => await ReproAsync(ctx, ct),
            "traceability" => BuildTraceability(ctx),
            _ => throw new InvalidOperationException($"tester: unknown param 'mode' ('{mode}'); expected full | repro | traceability."),
        };
    }

    // ───────────── full ─────────────

    private static async Task<AgentResult> FullAsync(AgentContext ctx, CancellationToken ct)
    {
        var report = await ctx.Tests.RunAsync(ctx.Workspace.Root, ctx.Catalog.Core.TestProject, null, TestTimeout, ct);
        ctx.Log($"tests: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped of {report.Total} (exit {report.ExitCode})");
        return new AgentResult()
            .Add("test-report", report)
            .Decide(report.Green ? "Full test run is green" : "Full test run is RED",
                $"Ran the complete suite of {ctx.Catalog.Core.TestProject}: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped, {report.Total} total in {report.DurationMs} ms (exit code {report.ExitCode}).",
                new[] { "Run only tests of changed slices (faster, hides cross-slice regressions)" },
                "Running everything costs build + test time but catches cross-slice regressions; the report is returned even when red so the exit gate (not the tester) decides.");
    }

    // ───────────── repro ─────────────

    private static async Task<AgentResult> ReproAsync(AgentContext ctx, CancellationToken ct)
    {
        var cat = ctx.Catalog;
        var rel = cat.Core.HardeningTestFile;
        var source = cat.ResolveSource(rel);
        if (!File.Exists(source))
            throw new InvalidOperationException($"repro: hardening test file '{rel}' not found in the component library ({source}).");

        var temp = Path.Combine(Path.GetTempPath(), "agentic-repro-" + Guid.NewGuid().ToString("N"));
        TestReport report;
        try
        {
            Directory.CreateDirectory(temp);
            ctx.Workspace.CopyTo(temp);
            var dest = Path.Combine(temp, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            report = await ctx.Tests.RunAsync(temp, cat.Core.TestProject, null, TestTimeout, ct);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ctx.Log($"repro: could not delete temp dir {temp}: {e.Message}"); }
        }

        var reproduced = report.Failed > 0;
        var summary = reproduced
            ? $"DEF reproduced: {report.Failed} hardening test(s) fail on the unpatched baseline"
            : report.Total == 0
                ? $"could not reproduce: the experiment produced no test results (exit code {report.ExitCode})"
                : "could not reproduce: all hardening tests pass on the baseline";
        ctx.Log("repro: " + summary);
        return new AgentResult()
            .Add("repro", new ReproReport(reproduced, report.Failed, report.Total, report.Failures, summary))
            .Decide(reproduced ? "Defect reproduced before fixing" : "Defect not reproduced",
                $"Copied the workspace to a temporary directory, added the hardening tests ({rel}) and ran the suite there: {report.Failed} of {report.Total} test(s) failed. The real workspace was not touched and the temporary copy was deleted.",
                new[] { "Fix first and only assert afterwards (no proof that the defect existed)", "Rely on static-analysis findings alone" },
                "Costs one extra build + test run, but turns a suspected defect into falsifiable evidence and gives the fix a before/after proof.");
    }

    // ───────────── traceability ─────────────

    private static AgentResult BuildTraceability(AgentContext ctx)
    {
        var plan = ctx.Read<TestPlan>("test-plan");
        var integration = ctx.ReadOrDefault<IntegrationReport>("integration");

        // acceptance id -> tests ("file::Method")
        var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in ctx.Workspace.List("**/*.cs").Select(p => p.Replace('\\', '/')).Where(p => p.StartsWith("tests/", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
            foreach (var (id, method) in ScanTests(ctx.Workspace.ReadText(file)))
            {
                if (!found.TryGetValue(id, out var set)) found[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add($"{file}::{method}");
            }

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (feature, criteria) in plan.AcceptanceByFeature)
            foreach (var c in criteria) owners.TryAdd(c.Id, feature);

        var matrix = plan.RequiredAcceptanceIds.Distinct(StringComparer.Ordinal).Select(id =>
        {
            var tests = found.TryGetValue(id, out var set) ? set.ToList() : new List<string>();
            var owner = owners.GetValueOrDefault(id) ?? ctx.Catalog.Features.Values.FirstOrDefault(f => f.Acceptance.Any(a => a.Id == id))?.Id ?? "unknown";
            return new TraceRow(id, owner, tests, tests.Count > 0);
        }).ToList();
        var missing = matrix.Where(r => !r.Covered).Select(r => r.AcceptanceId).ToList();
        var coverage = matrix.Count == 0 ? 1.0 : (double)(matrix.Count - missing.Count) / matrix.Count;

        var result = new AgentResult().Add("traceability", new Traceability(matrix, coverage, missing));
        var unmapped = integration is null ? new List<string>() : missing.Where(id => !integration.InstalledFeatures.Contains(matrix.First(r => r.AcceptanceId == id).Feature)).ToList();
        result.Decide(missing.Count == 0 ? "All acceptance criteria are traced to tests" : $"{missing.Count} acceptance criteria have no test",
            $"Scanned tests/**/*.cs for [Trait(\"AC\", ...)] attributes: {matrix.Count - missing.Count} of {matrix.Count} required criteria are covered ({ExecutionSupport.Percent(coverage)})."
            + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing)}." : "")
            + (unmapped.Count > 0 ? $" Of these, {string.Join(", ", unmapped)} belong to features that are not installed." : ""),
            new[] { "Infer coverage from test names (unreliable)", "Use code-coverage percentage as a proxy (does not prove criteria are asserted)" },
            "Relies on authors tagging tests with the criterion they verify; a tag proves intent, not assertion quality.");
        return result;
    }

    /// <summary>Extracts (acceptance id, test method) pairs: each trait binds to the next declared method; a trait placed before a class declaration applies to every method of that file after it.</summary>
    internal static IEnumerable<(string Id, string Method)> ScanTests(string content)
    {
        var methods = MethodRx.Matches(content).Select(m => (m.Index, Name: m.Groups[1].Value)).ToList();
        foreach (Match trait in TraitRx.Matches(content))
        {
            var id = trait.Groups[1].Value;
            var next = methods.FindIndex(m => m.Index > trait.Index);
            if (next < 0) continue;
            var between = content.Substring(trait.Index, methods[next].Index - trait.Index);
            if (ClassRx.IsMatch(between))
                foreach (var m in methods.Skip(next)) yield return (id, m.Name);
            else
                yield return (id, methods[next].Name);
        }
    }
}
