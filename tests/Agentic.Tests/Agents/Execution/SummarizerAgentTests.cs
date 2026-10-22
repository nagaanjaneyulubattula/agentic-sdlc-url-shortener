using System.Text.RegularExpressions;
using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class SummarizerAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();
    private readonly string _runDir = Path.Combine(Path.GetTempPath(), "agentic-test-summary-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); try { Directory.Delete(_runDir, true); } catch (IOException) { } }

    private static readonly string[] Headings =
    {
        "## 1. Outcome", "## 2. Plan and rationale", "## 3. Artifacts", "## 4. Risks, trade-offs and validation",
        "## 5. Assumptions", "## 6. Orchestration evidence", "## 7. Decision lineage", "## 8. Limitations",
    };

    // Shapes mirror what the engine's RunViews.StateForAgents / MetricsView / Lineage publish.
    private const string Metrics = """
        {"successRate":0.9091,"attemptSuccessRate":0.8333,"totalAttempts":12,"retries":1,"rollbacks":1,"fallbacks":1,"replans":1,"policyViolations":1,
         "approvalsRequested":2,"approvalsGranted":1,"approvalsRejected":1,"mttrMs":120.5,"endToEndMs":2500,"criticalPathMs":1800,
         "criticalPath":["analyse","implement:shorten","test"],"nodesDone":10,"nodesTotal":11,"approvalWaitMs":0,"perStage":{}}
        """;

    private const string State = """
        {"status":"Completed",
         "nodes":[
           {"id":"analyse","stage":"requirements","agent":"requirements-analyst","status":"Done","attempts":1,"retries":0,"rollbacks":0,"usedFallback":false},
           {"id":"implement:shorten","stage":"implementation","agent":"implementer","status":"Done","attempts":3,"retries":1,"rollbacks":1,"usedFallback":true,"error":"boom"},
           {"id":"test","stage":"verification","agent":"tester","status":"Failed","attempts":1,"retries":0,"rollbacks":0,"usedFallback":false,"error":"red suite"}],
         "approvals":[{"nodeId":"implement:hardening","kind":"approval","when":"before","decision":"approved","approver":"alice","comment":"ship the fix","risk":"high","reason":"sensitive path"}],
         "policyViolations":[{"rule":"SEC-004","severity":"block","nodeId":"implement:shorten","file":"src/UrlShortener.Api/X.cs","message":"weak randomness"}],
         "replans":[{"at":"2026-01-01T00:00:00Z","reason":"Requirement amended: add expiry","staleNodes":["design"]}],
         "changes":[{"path":"src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs","op":"create","nodeId":"implement:shorten"},
                    {"path":"src/UrlShortener.Api/Core/UrlValidator.cs","op":"modify","nodeId":"implement:hardening"}],
         "artifacts":[{"name":"requirements","version":2,"hash":"0123456789abcdef0123"},{"name":"design","version":1,"hash":"fedcba9876543210ffff"}],
         "mermaid":"flowchart LR\n  analyse[\"analyse<br/>Done\"]\n  implement_shorten[\"implement:shorten<br/>Done\"]\n  test[\"test<br/>Failed\"]\n  analyse --> implement_shorten\n  implement_shorten --> test\n"}
        """;

    private const string Lineage = """
        [{"id":"D1","nodeId":"analyse","title":"Treat redirects as implied","rationale":"A shortener without redirect is useless","alternatives":["ask the user"],"tradeoff":"may over-build","basedOn":[]},
         {"id":"D2","nodeId":"implement:shorten","title":"Install slice from library","rationale":"Vetted code","alternatives":[],"tradeoff":"limited flexibility","basedOn":[]},
         {"id":"D3","nodeId":"implement:shorten","title":"Clean-room reinstall","rationale":"Attempt 2 failed","alternatives":[],"tradeoff":"drops local edits","basedOn":[]}]
        """;

    private async Task<string> Summarize(Dictionary<string, object>? artifacts = null, IRunSnapshot? snapshot = null)
    {
        var ctx = Ctx.Make(_lib, _ws, artifacts: artifacts ?? Full(), snapshot: snapshot ?? new StubSnapshot(Metrics, State, Lineage), runDir: _runDir, title: "Harden the shortener");
        var r = await new SummarizerAgent().RunAsync(ctx, CancellationToken.None);
        var info = Ctx.Artifact<SummaryInfo>(r, "summary");
        return File.ReadAllText(info.Path);
    }

    private sealed record SummaryInfo(string Path, long Bytes);

    private static Dictionary<string, object> Full()
    {
        var a = Fixtures.GoArtifacts();
        a["test-plan"] = Fixtures.Plan();
        a["release-readiness"] = new ReleaseReadiness("GO",
            new() { new ChecklistItem("REL-01", "All tests green", "pass", "12 passed / 12 total"), new ChecklistItem("REL-03", "Security", "warn", "1 warning") },
            3, 1, new() { "Redeploy previous build" }, new() { "R-ALIAS-SQUAT: Alias squatting - Accepted: moderation needed" });
        a["repro"] = new ReproReport(true, 2, 10, new() { "x" }, "DEF reproduced: 2 hardening test(s) fail on the unpatched baseline");
        return a;
    }

    [Fact]
    public async Task ContainsAllEightH2Headings_InOrder()
    {
        var md = await Summarize();

        var found = Regex.Matches(md, @"^## .+$", RegexOptions.Multiline).Select(m => m.Value.Trim()).ToList();
        Assert.Equal(Headings, found);
    }

    [Fact]
    public async Task WritesToRunDirectory_AndReturnsPathAndByteCount()
    {
        var ctx = Ctx.Make(_lib, _ws, artifacts: Full(), snapshot: new StubSnapshot(Metrics, State, Lineage), runDir: _runDir);
        var r = await new SummarizerAgent().RunAsync(ctx, CancellationToken.None);

        var info = Ctx.Artifact<SummaryInfo>(r, "summary");
        Assert.Equal(Path.Combine(_runDir, "ENGINEERING_SUMMARY.md"), info.Path);
        Assert.True(File.Exists(info.Path));
        Assert.Equal(new FileInfo(info.Path).Length, info.Bytes);
        Assert.Empty(_ws.ChangedFiles);
    }

    [Fact]
    public async Task Outcome_ShowsScenarioAndReleaseDecision()
    {
        var md = await Summarize();
        var outcome = Section(md, 1);
        Assert.Contains("Harden the shortener", outcome);
        Assert.Contains("**GO**", outcome);
        Assert.Contains("12 passed / 12 total", outcome);
        Assert.Contains("DEF reproduced", outcome);
    }

    [Fact]
    public async Task Plan_HasTaskTable_Adrs_AndMermaidGraphWithStatuses()
    {
        var plan = Section(await Summarize(), 2);

        Assert.Contains("A service that shortens URLs", plan);
        Assert.Contains("| `implement:alias` | `alias` | slice | `implement:shorten` |", plan);
        Assert.Contains("ADR-1", plan);
        Assert.Contains("```mermaid", plan);
        Assert.Contains("flowchart LR", plan);
        Assert.Contains("analyse --> implement_shorten", plan);
        Assert.Contains("class analyse,implement_shorten done", plan);
        Assert.Contains("class test failed", plan);
    }

    [Fact]
    public async Task Mermaid_IsRebuiltFromNodes_WhenEngineGraphIsAbsent()
    {
        var state = """{"nodes":[{"id":"a","status":"Done"},{"id":"b:c","status":"Skipped","deps":["a"]}]}""";
        var plan = Section(await Summarize(snapshot: new StubSnapshot(Metrics, state, "[]")), 2);
        Assert.Contains("a --> b_c", plan);
        Assert.Contains("class b_c skipped", plan);
    }

    [Fact]
    public async Task Artifacts_ListNameVersionAndHashPrefix_AndGroupFilesByCreatedAndModified()
    {
        var art = Section(await Summarize(), 3);

        Assert.Contains("| `requirements` | 2 | `0123456789ab` |", art);
        Assert.Contains("**Created (1)**", art);
        Assert.Contains("**Modified (1)**", art);
        Assert.Contains("| `src/UrlShortener.Api/Features/Shorten` | `ShortenFeature.cs` |", art);
        Assert.Contains("| `src/UrlShortener.Api/Core` | `UrlValidator.cs` |", art);
    }

    [Fact]
    public async Task Risks_ShowRegisterTestTotalsTraceabilityChecklistAndResidual()
    {
        var risks = Section(await Summarize(), 4);

        Assert.Contains("R-COLLISION", risks);
        Assert.Contains("AC-SHORT-2 [covered]", risks);
        Assert.Contains("12 passed, 0 failed, 0 skipped, 12 total", risks);
        Assert.Contains("100% (2/2)", risks);
        Assert.Contains("passed: 10 file(s) scanned, 0 finding(s)", risks);
        Assert.Contains("| REL-03 | Security | WARN | 1 warning |", risks);
        Assert.Contains("R-ALIAS-SQUAT: Alias squatting - Accepted", risks);
    }

    [Fact]
    public async Task Assumptions_ListAmbiguitiesAnswersAndApprovals()
    {
        var req = Fixtures.Requirements(new() { new Ambiguity("AMB-1", "fast", "make it fast", "high", "How fast?", "p95 50ms", true, "p95 100ms") }) with
        {
            Answers = new() { ["AMB-1"] = "p95 100ms" },
        };
        var arts = Full(); arts["requirements"] = req;

        var s = Section(await Summarize(arts), 5);

        Assert.Contains("Links do not expire unless a TTL is given", s);
        Assert.Contains("| AMB-1 | fast | high | How fast? | p95 50ms | yes | p95 100ms |", s);
        Assert.Contains("`AMB-1`: p95 100ms", s);
        Assert.Contains("| `implement:hardening` | approval | before | high | sensitive path | approved | alice | ship the fix |", s);
    }

    [Fact]
    public async Task Orchestration_ShowsMetricsAndWhatActuallyHappened()
    {
        var o = Section(await Summarize(), 6);

        Assert.Contains("| Success rate (nodes) | 90.9% (10/11 nodes done) |", o);
        Assert.Contains("| Attempt success rate | 83.3% (12 attempts) |", o);
        Assert.Contains("| Retries | 1 |", o);
        Assert.Contains("| MTTR | 120.5 ms |", o);
        Assert.Contains("| End-to-end latency | 2.50 s |", o);
        Assert.Contains("`analyse` -> `implement:shorten` -> `test`", o);
        Assert.Contains("| Approvals requested / granted / rejected | 2 / 1 / 1 |", o);
        Assert.Contains("`implement:shorten`: 1 retry (3 attempt(s)) - last error: boom", o);
        Assert.Contains("`implement:shorten`: 1 rollback(s)", o);
        Assert.Contains("`implement:shorten`: fallback agent used", o);
        Assert.Contains("Requirement amended: add expiry (invalidated: design)", o);
        Assert.Contains("SEC-004 [block] at `implement:shorten`: weak randomness", o);
        Assert.Contains("`test`: failed - last error: red suite", o);
    }

    [Fact]
    public async Task Lineage_GroupsDecisionsByNode()
    {
        var l = Section(await Summarize(), 7);

        Assert.Contains("3 decision(s) recorded by 2 node(s)", l);
        var shorten = l.IndexOf("### `implement:shorten`", StringComparison.Ordinal);
        Assert.True(shorten > 0);
        Assert.Contains("Treat redirects as implied (alternatives: ask the user)", l);
        Assert.Contains("Clean-room reinstall", l[shorten..]);
        Assert.Single(Regex.Matches(l, "### `implement:shorten`"));
    }

    [Fact]
    public async Task Limitations_AreSpecific()
    {
        var lim = Section(await Summarize(), 8);
        foreach (var needle in new[] { "rule-based", "in-memory", "authentication", "single-instance", "scripted approver", "Static-analysis", "build and test time" })
            Assert.Contains(needle, lim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeStoppedRun_WithoutOptionalArtifactsOrSnapshot_StillProducesAllSections()
    {
        var ctx = Ctx.Make(_lib, _ws, artifacts: new(), snapshot: null, runDir: _runDir);
        var r = await new SummarizerAgent().RunAsync(ctx, CancellationToken.None);
        var md = File.ReadAllText(Ctx.Artifact<SummaryInfo>(r, "summary").Path);

        Assert.Equal(Headings, Regex.Matches(md, @"^## .+$", RegexOptions.Multiline).Select(m => m.Value.Trim()));
        Assert.Contains("NOT ISSUED", md);
        Assert.Contains("run snapshot was unavailable", md);
    }

    [Fact]
    public void Autonomy_ReadsSnapshotButWritesNoWorkspaceFiles()
    {
        var a = new SummarizerAgent();
        Assert.Empty(a.Autonomy.WriteGlobs);
        Assert.True(a.Autonomy.ReadsRunSnapshot);
        Assert.Equal("summarizer", a.Name);
    }

    private static string Section(string md, int n)
    {
        var start = md.IndexOf(Headings[n - 1], StringComparison.Ordinal);
        Assert.True(start >= 0, "missing heading " + Headings[n - 1]);
        var end = n < Headings.Length ? md.IndexOf(Headings[n], start, StringComparison.Ordinal) : md.Length;
        return md[start..end];
    }
}
