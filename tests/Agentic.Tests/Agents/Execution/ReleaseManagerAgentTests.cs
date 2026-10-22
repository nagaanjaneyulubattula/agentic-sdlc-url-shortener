using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class ReleaseManagerAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); }

    private async Task<(ReleaseReadiness Report, AgentResult Result)> Run(Dictionary<string, object> artifacts, IRunSnapshot? snapshot = null)
    {
        var r = await new ReleaseManagerAgent().RunAsync(Ctx.Make(_lib, _ws, artifacts: artifacts, snapshot: snapshot ?? Fixtures.Snapshot()), CancellationToken.None);
        return (Ctx.Artifact<ReleaseReadiness>(r, "release-readiness"), r);
    }

    private static ChecklistItem Item(ReleaseReadiness r, string id) => r.Checklist.Single(c => c.Id == id);

    [Fact]
    public async Task Go_WhenAllEvidenceIsGreen()
    {
        var (report, result) = await Run(Fixtures.GoArtifacts());

        Assert.Equal("GO", report.Decision);
        Assert.DoesNotContain(report.Checklist, c => c.Status == "fail");
        Assert.Equal(new[] { "REL-01", "REL-02", "REL-03", "REL-04", "REL-05", "REL-07", "REL-08" }, report.Checklist.Select(c => c.Id));
        Assert.Equal("12 passed / 12 total", Item(report, "REL-01").Evidence);
        Assert.Equal("GO", result.Decisions.Single(d => d.Title.StartsWith("Release decision")).Title.Split(' ').Last());
    }

    [Fact]
    public async Task NoGo_WhenTestsAreRed()
    {
        var arts = Fixtures.GoArtifacts();
        arts["test-report"] = Reports.Red(2, 12);

        var (report, _) = await Run(arts);

        Assert.Equal("NO-GO", report.Decision);
        Assert.Equal("fail", Item(report, "REL-01").Status);
        Assert.Contains("10 passed / 12 total", Item(report, "REL-01").Evidence);
    }

    [Fact]
    public async Task NoGo_WhenAcceptanceCoverageBelowOne_ListsMissing()
    {
        var arts = Fixtures.GoArtifacts();
        arts["traceability"] = Fixtures.Trace(0.5, "AC-ALIAS-1");

        var (report, _) = await Run(arts);

        Assert.Equal("NO-GO", report.Decision);
        Assert.Equal("fail", Item(report, "REL-02").Status);
        Assert.Contains("AC-ALIAS-1", Item(report, "REL-02").Evidence);
        // risk R-ALIAS-SQUAT is validated by AC-ALIAS-1, which is not covered
        Assert.Equal("fail", Item(report, "REL-08").Status);
        Assert.Contains("R-ALIAS-SQUAT", Item(report, "REL-08").Evidence);
    }

    [Fact]
    public async Task NoGo_WhenHighSeverityAmbiguityUnresolved_ButGoWhenResolvedOrLow()
    {
        var open = new Ambiguity("AMB-1", "fast", "make it fast", "high", "How fast?", "p95 50ms", false, null);
        var arts = Fixtures.GoArtifacts();
        arts["requirements"] = Fixtures.Requirements(new() { open });
        var (blocked, _) = await Run(arts);
        Assert.Equal("NO-GO", blocked.Decision);
        Assert.Equal("fail", Item(blocked, "REL-04").Status);
        Assert.Contains("AMB-1", Item(blocked, "REL-04").Evidence);

        arts["requirements"] = Fixtures.Requirements(new() { open with { Resolved = true, Resolution = "50ms" }, open with { Id = "AMB-2", Severity = "low" } });
        var (ok, _) = await Run(arts);
        Assert.Equal("GO", ok.Decision);
    }

    [Fact]
    public async Task SecurityWarningsOnly_AreWarnButStillGo_BlockFindingsFail()
    {
        var arts = Fixtures.GoArtifacts();
        arts["security-report"] = new SecurityReport(10, new() { new SecurityFinding("QUAL-001", "warn", "a.cs", 1, "empty catch") }, true);
        var (warn, _) = await Run(arts);
        Assert.Equal("warn", Item(warn, "REL-03").Status);
        Assert.Equal("GO", warn.Decision);

        arts["security-report"] = new SecurityReport(10, new() { new SecurityFinding("SEC-004", "block", "a.cs", 1, "random") }, false);
        var (block, _) = await Run(arts);
        Assert.Equal("fail", Item(block, "REL-03").Status);
        Assert.Equal("NO-GO", block.Decision);
    }

    [Fact]
    public async Task NoGo_WhenDocumentationIsIncomplete()
    {
        var arts = Fixtures.GoArtifacts();
        arts["docs"] = new DocsReport(new() { new FileChange("README.md", "create", "x", 1) });
        var (report, _) = await Run(arts);
        Assert.Equal("fail", Item(report, "REL-05").Status);
        Assert.Contains("docs/API.md", Item(report, "REL-05").Evidence);
    }

    [Fact]
    public async Task Rel06_OnlyPresentWithRepro_PassesWhenReproducedAndGreen()
    {
        var arts = Fixtures.GoArtifacts();
        arts["repro"] = new ReproReport(true, 2, 10, new() { "x" }, "DEF reproduced: 2 hardening test(s) fail on the unpatched baseline");
        var (report, _) = await Run(arts);
        Assert.Equal("pass", Item(report, "REL-06").Status);
        Assert.Equal("GO", report.Decision);
    }

    [Fact]
    public async Task Rel06_FailsWhenDefectWasNotReproduced_OrTestsAreRedAfterFix()
    {
        var arts = Fixtures.GoArtifacts();
        arts["repro"] = new ReproReport(false, 0, 10, new(), "could not reproduce: all hardening tests pass on the baseline");
        var (notRepro, _) = await Run(arts);
        Assert.Equal("fail", Item(notRepro, "REL-06").Status);
        Assert.Equal("NO-GO", notRepro.Decision);

        arts["repro"] = new ReproReport(true, 2, 10, new(), "DEF reproduced");
        arts["test-report"] = Reports.Red();
        var (red, _) = await Run(arts);
        Assert.Equal("fail", Item(red, "REL-06").Status);
    }

    [Fact]
    public async Task CountsChangesAndBuildsRollbackPlan_FromRunState()
    {
        var (report, _) = await Run(Fixtures.GoArtifacts());

        Assert.Equal(3, report.FilesCreated);   // Shorten, Alias, FeatureRegistry (created then modified => created)
        Assert.Equal(1, report.FilesModified);  // UrlValidator
        Assert.Contains("Restore pre-change version of src/UrlShortener.Api/Core/UrlValidator.cs (journal revert of node implement:hardening)", report.RollbackPlan);
        Assert.Contains("Delete src/UrlShortener.Api/Features/Alias/AliasFeature.cs (revert node implement:alias)", report.RollbackPlan);
        Assert.Equal("Redeploy previous build and keep the baseline snapshot as rollback target", report.RollbackPlan[^1]);
        Assert.Equal(5, report.RollbackPlan.Count);
    }

    [Fact]
    public async Task BlastRadiusAboveTheLimit_FailsRel07()
    {
        var changes = string.Join(",", Enumerable.Range(0, ChangeControl.MaxFilesChangedPerRun + 1)
            .Select(i => $"{{\"path\":\"src/F{i}.cs\",\"op\":\"create\",\"nodeId\":\"n\"}}"));
        var (report, _) = await Run(Fixtures.GoArtifacts(), Fixtures.Snapshot($"{{\"changes\":[{changes}]}}"));

        Assert.Equal("fail", Item(report, "REL-07").Status);
        Assert.Equal("NO-GO", report.Decision);
    }

    [Fact]
    public async Task ResidualRisks_AreThoseWhoseResidualStartsWithAccepted()
    {
        var (report, _) = await Run(Fixtures.GoArtifacts());
        var residual = Assert.Single(report.ResidualRisks);
        Assert.Contains("R-ALIAS-SQUAT", residual);
        Assert.Contains("Accepted", residual);
    }

    [Fact]
    public async Task MissingEvidence_IsNoGo()
    {
        var (report, _) = await Run(new Dictionary<string, object>());
        Assert.Equal("NO-GO", report.Decision);
        Assert.All(report.Checklist.Where(c => c.Id != "REL-07"), c => Assert.Equal("fail", c.Status)); // blast radius is independent of missing evidence
    }

    [Fact]
    public async Task WorksWithoutSnapshot()
    {
        var r = await new ReleaseManagerAgent().RunAsync(Ctx.Make(_lib, _ws, artifacts: Fixtures.GoArtifacts()), CancellationToken.None);
        var report = Ctx.Artifact<ReleaseReadiness>(r, "release-readiness");
        Assert.Equal(0, report.FilesCreated);
        Assert.Single(report.RollbackPlan);
    }

    [Fact]
    public void Autonomy_NoWrites()
    {
        var a = new ReleaseManagerAgent();
        Assert.Empty(a.Autonomy.WriteGlobs);
        Assert.True(a.Autonomy.ReadsRunSnapshot); // reads State().changes for blast radius + rollback plan
        Assert.Equal("release-manager", a.Name);
    }
}
