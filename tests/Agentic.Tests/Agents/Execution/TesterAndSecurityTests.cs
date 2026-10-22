using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class TesterAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); }

    private Task<AgentResult> Run(string mode, FakeTestRunner? runner = null, Dictionary<string, object>? artifacts = null) =>
        new TesterAgent().RunAsync(Ctx.Make(_lib, _ws, Ctx.P(("mode", mode)), artifacts, runner), CancellationToken.None);

    [Fact]
    public async Task Full_RunsTheTestProjectInTheWorkspace()
    {
        var runner = new FakeTestRunner();
        var r = await Run("full", runner);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(_ws.Root, call.Root);
        Assert.Equal(_lib.Catalog.Core.TestProject, call.Project);
        Assert.True(Ctx.Artifact<TestReport>(r, "test-report").Green);
    }

    [Fact]
    public async Task Full_ReturnsRedReportInsteadOfThrowing()
    {
        var r = await Run("full", new FakeTestRunner { Respond = _ => Reports.Red(3) });
        var report = Ctx.Artifact<TestReport>(r, "test-report");
        Assert.False(report.Green);
        Assert.Equal(3, report.Failed);
        Assert.Contains(r.Decisions, d => d.Title.Contains("RED"));
    }

    [Fact]
    public async Task Repro_Reproduced_WhenHardeningTestsFailOnTheBaselineCopy()
    {
        _ws.Seed("src/UrlShortener.Api/Core/UrlValidator.cs", "// vulnerable\n");
        string? seenRoot = null; bool hadHardening = false, hadWorkspaceFile = false;
        var runner = new FakeTestRunner
        {
            Respond = root =>
            {
                seenRoot = root;
                hadHardening = File.Exists(Path.Combine(root, "tests", "UrlShortener.Tests", "Core", "HardeningTests.cs"));
                hadWorkspaceFile = File.Exists(Path.Combine(root, "src", "UrlShortener.Api", "Core", "UrlValidator.cs"));
                return Reports.Red(2, 10);
            },
        };

        var r = await Run("repro", runner);

        var repro = Ctx.Artifact<ReproReport>(r, "repro");
        Assert.True(repro.Reproduced);
        Assert.Equal(2, repro.FailingTests);
        Assert.Equal(10, repro.TotalTests);
        Assert.Equal("DEF reproduced: 2 hardening test(s) fail on the unpatched baseline", repro.Summary);
        Assert.True(hadHardening && hadWorkspaceFile);
        Assert.NotEqual(_ws.Root, seenRoot);
        Assert.False(Directory.Exists(seenRoot), "temp dir must be deleted");
        Assert.False(_ws.Exists(_lib.Catalog.Core.HardeningTestFile), "real workspace must be untouched");
        Assert.Empty(_ws.ChangedFiles);
        Assert.NotEmpty(r.Decisions);
    }

    [Fact]
    public async Task Repro_NotReproduced_WhenAllPass()
    {
        var repro = Ctx.Artifact<ReproReport>(await Run("repro", new FakeTestRunner { Respond = _ => Reports.Green(8) }), "repro");
        Assert.False(repro.Reproduced);
        Assert.StartsWith("could not reproduce", repro.Summary);
    }

    [Fact]
    public async Task Repro_DeletesTempDirEvenWhenTheRunnerThrows()
    {
        string? seen = null;
        var runner = new FakeTestRunner { Respond = root => { seen = root; throw new IOException("runner exploded"); } };
        await Assert.ThrowsAsync<IOException>(() => Run("repro", runner));
        Assert.NotNull(seen);
        Assert.False(Directory.Exists(seen));
    }

    [Fact]
    public async Task Traceability_BuildsMatrixWithCoveredAndMissing()
    {
        _ws.Seed("tests/UrlShortener.Tests/Features/Shorten/ShortenTests.cs", FakeLibrary.Tests("Shorten", ("AC-SHORT-1", "Creates_Link"), ("AC-SHORT-2", "Rejects_Invalid_Url")));
        _ws.Seed("tests/UrlShortener.Tests/Features/Alias/AliasTests.cs", FakeLibrary.Tests("Alias", ("AC-ALIAS-1", "Uses_Alias")));
        var arts = new Dictionary<string, object> { ["test-plan"] = Fixtures.Plan("AC-SHORT-1", "AC-SHORT-2", "AC-ALIAS-1", "AC-ALIAS-2"), ["integration"] = Fixtures.Integration() };

        var t = Ctx.Artifact<Traceability>(await Run("traceability", artifacts: arts), "traceability");

        Assert.Equal(4, t.Matrix.Count);
        Assert.Equal(0.75, t.Coverage);
        Assert.Equal(new[] { "AC-ALIAS-2" }, t.Missing);
        var row = t.Matrix.Single(m => m.AcceptanceId == "AC-SHORT-1");
        Assert.Equal("shorten", row.Feature);
        Assert.True(row.Covered);
        Assert.Equal(new[] { "tests/UrlShortener.Tests/Features/Shorten/ShortenTests.cs::Creates_Link" }, row.Tests);
        Assert.False(t.Matrix.Single(m => m.AcceptanceId == "AC-ALIAS-2").Covered);
    }

    [Fact]
    public async Task Traceability_FullCoverage_IsOne_AndEmptyRequiredIsOne()
    {
        _ws.Seed("tests/UrlShortener.Tests/Features/Shorten/ShortenTests.cs", FakeLibrary.Tests("Shorten", ("AC-SHORT-1", "Creates_Link")));
        var full = Ctx.Artifact<Traceability>(await Run("traceability", artifacts: new() { ["test-plan"] = Fixtures.Plan("AC-SHORT-1"), ["integration"] = Fixtures.Integration() }), "traceability");
        Assert.Equal(1.0, full.Coverage);
        Assert.Empty(full.Missing);

        var plan = Fixtures.Plan() with { RequiredAcceptanceIds = new() };
        var empty = Ctx.Artifact<Traceability>(await Run("traceability", artifacts: new() { ["test-plan"] = plan, ["integration"] = Fixtures.Integration() }), "traceability");
        Assert.Equal(1.0, empty.Coverage);
        Assert.Empty(empty.Matrix);
    }

    [Fact]
    public async Task Traceability_HandlesStackedTraitsAsyncMethodsAndClassLevelTraits()
    {
        _ws.Seed("tests/UrlShortener.Tests/Mixed/MixedTests.cs", """
            namespace UrlShortener.Tests;
            public class MixedTests
            {
                [Fact]
                [Trait("AC", "AC-SHORT-1")]
                [Trait("AC", "AC-SHORT-2")]
                public async Task Both_Criteria() { await Task.CompletedTask; }

                [Fact] public void Untagged() { }
            }

            [Trait("AC", "AC-REDIR-1")]
            public class ClassLevelTests
            {
                [Fact] public void First() { }
                [Fact] public void Second() { }
            }
            """);
        var arts = new Dictionary<string, object> { ["test-plan"] = Fixtures.Plan("AC-SHORT-1", "AC-SHORT-2", "AC-REDIR-1"), ["integration"] = Fixtures.Integration() };

        var t = Ctx.Artifact<Traceability>(await Run("traceability", artifacts: arts), "traceability");

        const string f = "tests/UrlShortener.Tests/Mixed/MixedTests.cs";
        Assert.Equal(new[] { $"{f}::Both_Criteria" }, t.Matrix.Single(m => m.AcceptanceId == "AC-SHORT-1").Tests);
        Assert.Equal(new[] { $"{f}::Both_Criteria" }, t.Matrix.Single(m => m.AcceptanceId == "AC-SHORT-2").Tests);
        Assert.Equal(new[] { $"{f}::First", $"{f}::Second" }, t.Matrix.Single(m => m.AcceptanceId == "AC-REDIR-1").Tests);
        Assert.Equal(1.0, t.Coverage);
    }

    [Fact]
    public async Task Traceability_IgnoresNonTestFilesAndNonAcTraits()
    {
        _ws.Seed("src/UrlShortener.Api/Features/Shorten/Fake.cs", "[Trait(\"AC\", \"AC-SHORT-1\")] public void NotATest() { }");
        _ws.Seed("tests/UrlShortener.Tests/Other.cs", "[Trait(\"Category\", \"AC-SHORT-1\")]\n[Fact] public void Other() { }");
        var t = Ctx.Artifact<Traceability>(await Run("traceability", artifacts: new() { ["test-plan"] = Fixtures.Plan("AC-SHORT-1"), ["integration"] = Fixtures.Integration() }), "traceability");
        Assert.Equal(new[] { "AC-SHORT-1" }, t.Missing);
    }

    [Fact]
    public async Task UnknownMode_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("nope"));
    }

    [Fact]
    public void Autonomy_CanRunTestsButWriteNothing()
    {
        var a = new TesterAgent();
        Assert.Empty(a.Autonomy.WriteGlobs);
        Assert.True(a.Autonomy.CanRunTests);
        Assert.Equal("tester", a.Name);
    }
}

public sealed class SecurityReviewerAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); }

    private Task<AgentResult> Run() => new SecurityReviewerAgent().RunAsync(Ctx.Make(_lib, _ws), CancellationToken.None);

    [Fact]
    public async Task FlagsRandomAndHttpClient_AndFailsTheReport()
    {
        _ws.Seed("src/UrlShortener.Api/Features/Shorten/Bad.cs", "var r = new Random(42);\nvar c = new HttpClient();\n");

        var r = await Run();

        var report = Ctx.Artifact<SecurityReport>(r, "security-report");
        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f.Rule == "SEC-004" && f.Severity == "block" && f.Line == 1);
        Assert.Contains(report.Findings, f => f.Rule == "SEC-003" && f.Line == 2);
        Assert.Contains(r.Decisions, d => d.Title.StartsWith("SEC-004"));
        Assert.Contains(r.Decisions, d => d.Title.StartsWith("SEC-003"));
    }

    [Fact]
    public async Task AddsOneDecisionPerDistinctRule()
    {
        _ws.Seed("src/UrlShortener.Api/A.cs", "new Random(1); new Random(2);");
        _ws.Seed("src/UrlShortener.Api/B.cs", "new Random(3);");
        var r = await Run();
        Assert.Single(r.Decisions, d => d.Title.StartsWith("SEC-004"));
        Assert.Contains("3 hit(s) in 2 file(s)", r.Decisions.Single(d => d.Title.StartsWith("SEC-004")).Title);
    }

    [Fact]
    public async Task CleanWorkspace_Passes_WithNoFindingsDecision()
    {
        _ws.Seed("src/UrlShortener.Api/Features/Shorten/Good.cs", "namespace X; public class Good { }");
        var r = await Run();
        var report = Ctx.Artifact<SecurityReport>(r, "security-report");
        Assert.True(report.Passed);
        Assert.Empty(report.Findings);
        Assert.Equal("No policy findings", Assert.Single(r.Decisions).Title);
    }

    [Fact]
    public async Task WarningsOnly_StillPassesButIsReported()
    {
        _ws.Seed("src/UrlShortener.Api/Warn.cs", "try { } catch { }");
        var r = await Run();
        var report = Ctx.Artifact<SecurityReport>(r, "security-report");
        Assert.True(report.Passed);
        Assert.Equal("QUAL-001", Assert.Single(report.Findings).Rule);
    }

    [Fact]
    public async Task DetectsSecretsAsCritical()
    {
        _ws.Seed("src/UrlShortener.Api/Secrets.cs", "var k = \"AKIAABCDEFGHIJKLMNOP\";");
        var report = Ctx.Artifact<SecurityReport>(await Run(), "security-report");
        Assert.Contains(report.Findings, f => f.Rule == "SEC-001" && f.Severity == "critical");
        Assert.False(report.Passed);
    }

    [Fact]
    public void Autonomy_HasNoWritesAndCannotRunTests()
    {
        var a = new SecurityReviewerAgent();
        Assert.Empty(a.Autonomy.WriteGlobs);
        Assert.False(a.Autonomy.CanRunTests);
        Assert.Equal("security-reviewer", a.Name);
    }
}
