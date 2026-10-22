using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class ImplementerAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); }

    private Task<AgentResult> Run(IAgent agent, params (string, string)[] p) =>
        agent.RunAsync(Ctx.Make(_lib, _ws, Ctx.P(p)), CancellationToken.None);

    private Task<AgentResult> Slice(string feature, IAgent? agent = null) =>
        Run(agent ?? new ImplementerAgent(), ("mode", "slice"), ("feature", feature));

    private async Task InstallBase()
    {
        await Slice("shorten");
        await Slice("redirect");
        _ws.ClearChanges();
    }

    [Fact]
    public async Task Scaffold_CopiesEveryCoreFileAndGitignore()
    {
        var r = await Run(new ImplementerAgent(), ("mode", "scaffold"));

        foreach (var rel in _lib.Catalog.CoreFiles(baseline: false))
        {
            Assert.True(_ws.Exists(rel), $"missing {rel}");
            Assert.Equal(File.ReadAllText(_lib.Catalog.ResolveSource(rel)), _ws.ReadText(rel));
        }
        Assert.Equal("bin/\nobj/\n", _ws.ReadText(".gitignore"));
        var report = Ctx.Artifact<ImplReport>(r, "scaffold");
        Assert.Equal("core", report.Feature);
        Assert.Equal("assemble-from-library", report.Strategy);
        Assert.Equal(_lib.Catalog.CoreFiles(false).Count + 1, report.Files.Count);
        Assert.All(report.Files, f => Assert.Equal("create", f.Op));
        Assert.Contains(report.Files, f => f.Path == ".gitignore");
        Assert.NotEmpty(r.Decisions);
    }

    [Fact]
    public async Task Scaffold_RunTwice_ReportsUnchanged()
    {
        await Run(new ImplementerAgent(), ("mode", "scaffold"));
        _ws.ClearChanges();
        var r = await Run(new ImplementerAgent(), ("mode", "scaffold"));
        Assert.All(Ctx.Artifact<ImplReport>(r, "scaffold").Files, f => Assert.Equal("unchanged", f.Op));
        Assert.Empty(_ws.ChangedFiles);
    }

    [Fact]
    public async Task Slice_NewFeature_ReportsCreateOpsAndProducesImplArtifact()
    {
        var r = await Slice("shorten");

        var report = Ctx.Artifact<ImplReport>(r, "impl:shorten");
        Assert.Equal("shorten", report.Feature);
        Assert.Equal("assemble-from-library", report.Strategy);
        Assert.Equal(_lib.Catalog.FeatureFiles("shorten").Count, report.Files.Count);
        Assert.Equal(3, report.Files.Count);
        Assert.All(report.Files, f => { Assert.Equal("create", f.Op); Assert.True(_ws.Exists(f.Path)); Assert.True(f.Bytes > 0); });
        Assert.Contains("tests/UrlShortener.Tests/Features/Shorten/ShortenTests.cs", report.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Slice_Reinstall_ReportsUnchangedAndDoesNotRewrite()
    {
        await Slice("shorten");
        _ws.ClearChanges();

        var r = await Slice("shorten");

        Assert.All(Ctx.Artifact<ImplReport>(r, "impl:shorten").Files, f => Assert.Equal("unchanged", f.Op));
        Assert.Empty(_ws.ChangedFiles);
    }

    [Fact]
    public async Task Slice_LocallyEditedFile_ReportsModifyAndRestoresLibraryContent()
    {
        await Slice("shorten");
        var edited = "src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs";
        _ws.Seed(edited, "// tampered\n");
        _ws.ClearChanges();

        var r = await Slice("shorten");

        var files = Ctx.Artifact<ImplReport>(r, "impl:shorten").Files;
        Assert.Equal("modify", files.Single(f => f.Path == edited).Op);
        Assert.Equal(2, files.Count(f => f.Op == "unchanged"));
        Assert.Equal(File.ReadAllText(_lib.Catalog.ResolveSource(edited)), _ws.ReadText(edited));
    }

    [Fact]
    public async Task Slice_MissingRequiredSlice_ThrowsWithClearMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Slice("alias"));
        Assert.Contains("alias", ex.Message);
        Assert.Contains("shorten", ex.Message);
        Assert.False(_ws.Exists("src/UrlShortener.Api/Features/Alias/AliasFeature.cs"));
    }

    [Fact]
    public async Task Slice_RequirementInstalled_Succeeds()
    {
        await InstallBase();
        var r = await Slice("alias");
        Assert.True(r.Artifacts.ContainsKey("impl:alias"));
    }

    [Fact]
    public async Task Slice_FeatureWithoutLibrarySources_Throws()
    {
        await InstallBase();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Slice("expiry"));
        Assert.Contains("no source files", ex.Message);
    }

    [Fact]
    public async Task Slice_MissingFeatureParam_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImplementerAgent(), ("mode", "slice")));
        Assert.Contains("feature", ex.Message);
    }

    [Fact]
    public async Task UnknownMode_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImplementerAgent(), ("mode", "bogus")));
        Assert.Contains("mode", ex.Message);
    }

    [Fact]
    public async Task Patch_AppliesLibraryFilesWithApplyPatchStrategy()
    {
        _ws.Seed("src/UrlShortener.Api/Core/UrlValidator.cs", "// vulnerable v1\n");
        _ws.Seed("src/UrlShortener.Api/Core/CodeGenerator.cs", File.ReadAllText(_lib.Catalog.ResolveSource("src/UrlShortener.Api/Core/CodeGenerator.cs")));

        var r = await Run(new ImplementerAgent(), ("mode", "patch"), ("feature", "hardening"));

        var report = Ctx.Artifact<ImplReport>(r, "impl:hardening");
        Assert.Equal("apply-patch", report.Strategy);
        Assert.Equal("modify", report.Files.Single(f => f.Path.EndsWith("UrlValidator.cs")).Op);
        Assert.Equal("unchanged", report.Files.Single(f => f.Path.EndsWith("CodeGenerator.cs")).Op);
        Assert.Equal("create", report.Files.Single(f => f.Path.EndsWith("HardeningTests.cs")).Op);
    }

    [Fact]
    public async Task CleanRoom_RemovesStaleSliceFilesAndReinstalls()
    {
        await Slice("shorten");
        _ws.Seed("src/UrlShortener.Api/Features/Shorten/Stale.cs", "// left over from a failed attempt\n");
        _ws.Seed("tests/UrlShortener.Tests/Features/Shorten/StaleTests.cs", "// stale\n");
        _ws.Seed("src/UrlShortener.Api/Features/Redirect/Keep.cs", "// other slice must stay\n");
        _ws.Seed("src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs", "// corrupted\n");
        _ws.ClearChanges();

        var r = await Slice("shorten", new ImplementerCleanRoomAgent());

        Assert.False(_ws.Exists("src/UrlShortener.Api/Features/Shorten/Stale.cs"));
        Assert.False(_ws.Exists("tests/UrlShortener.Tests/Features/Shorten/StaleTests.cs"));
        Assert.True(_ws.Exists("src/UrlShortener.Api/Features/Redirect/Keep.cs"));
        Assert.Equal(File.ReadAllText(_lib.Catalog.ResolveSource("src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs")), _ws.ReadText("src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs"));

        var report = Ctx.Artifact<ImplReport>(r, "impl:shorten");
        Assert.Equal("clean-room-reinstall", report.Strategy);
        Assert.Equal(2, report.Files.Count(f => f.Op == "delete"));
        Assert.Equal("modify", report.Files.Single(f => f.Path.EndsWith("ShortenFeature.cs")).Op);
        Assert.Equal("unchanged", report.Files.Single(f => f.Path.EndsWith("ShortenModels.cs")).Op);
    }

    [Fact]
    public async Task CleanRoom_StillEnforcesRequirements()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Slice("alias", new ImplementerCleanRoomAgent()));
    }

    [Fact]
    public async Task CleanRoom_NonSliceModes_BehaveLikePrimary()
    {
        var scaffold = await Run(new ImplementerCleanRoomAgent(), ("mode", "scaffold"));
        Assert.Equal("assemble-from-library", Ctx.Artifact<ImplReport>(scaffold, "scaffold").Strategy);
        var patch = await Run(new ImplementerCleanRoomAgent(), ("mode", "patch"), ("feature", "hardening"));
        Assert.Equal("apply-patch", Ctx.Artifact<ImplReport>(patch, "impl:hardening").Strategy);
    }

    [Fact]
    public async Task Integrate_WritesRegistryMatchingRenderRegistry()
    {
        await InstallBase();
        await Slice("alias");

        var r = await Run(new ImplementerAgent(), ("mode", "integrate"));

        var report = Ctx.Artifact<IntegrationReport>(r, "integration");
        var expected = _lib.Catalog.RenderRegistry(new[] { "shorten", "redirect", "alias" });
        Assert.Equal(_lib.Catalog.Core.RegistryPath, report.RegistryPath);
        Assert.Equal(expected, _ws.ReadText(report.RegistryPath));
        Assert.Equal(new[] { "shorten", "redirect", "alias" }, report.InstalledFeatures);
        Assert.Equal("create", Assert.Single(report.Files).Op);
        Assert.Contains("new UrlShortener.Api.Features.Alias.AliasFeature()", _ws.ReadText(report.RegistryPath));
    }

    [Fact]
    public async Task Integrate_IncludesHardeningOnlyWhenItsTestFileExists()
    {
        await InstallBase();
        var without = Ctx.Artifact<IntegrationReport>(await Run(new ImplementerAgent(), ("mode", "integrate")), "integration");
        Assert.DoesNotContain("hardening", without.InstalledFeatures);

        _ws.Seed(_lib.Catalog.Core.HardeningTestFile, "// tests\n");
        var with = Ctx.Artifact<IntegrationReport>(await Run(new ImplementerAgent(), ("mode", "integrate")), "integration");
        Assert.Equal(new[] { "hardening", "shorten", "redirect" }, with.InstalledFeatures);
        Assert.Equal("unchanged", with.Files.Single().Op);
    }

    [Fact]
    public async Task Integrate_SliceWithoutItsRequirement_Throws()
    {
        _ws.Seed("src/UrlShortener.Api/Features/Alias/AliasFeature.cs", "// alias only\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImplementerAgent(), ("mode", "integrate")));
        Assert.Contains("alias", ex.Message);
        Assert.Contains("shorten", ex.Message);
    }

    [Fact]
    public async Task Integrate_NothingInstalled_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImplementerAgent(), ("mode", "integrate")));
    }

    [Fact]
    public void Autonomy_AllowsSrcTestsAndGitignoreOnly_ForBothImplementers()
    {
        foreach (IAgent a in new IAgent[] { new ImplementerAgent(), new ImplementerCleanRoomAgent() })
        {
            Assert.Equal(new[] { "src/**", "tests/**", ".gitignore" }, a.Autonomy.WriteGlobs);
            Assert.False(a.Autonomy.CanRunTests);
        }
        Assert.Equal("implementer", ImplementerAgent.AgentName);
        Assert.Equal("implementer-clean-room", ImplementerCleanRoomAgent.AgentName);
    }
}
