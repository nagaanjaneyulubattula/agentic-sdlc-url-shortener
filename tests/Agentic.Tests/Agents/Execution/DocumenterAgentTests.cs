using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class DocumenterAgentTests : IDisposable
{
    private readonly FakeLibrary _lib = new();
    private readonly TempWorkspace _ws = new();

    public void Dispose() { _lib.Dispose(); _ws.Dispose(); }

    private Dictionary<string, object> Inputs(params string[] features) => new()
    {
        ["requirements"] = Fixtures.Requirements(),
        ["design"] = Fixtures.Design(features: features),
        ["integration"] = Fixtures.Integration(features),
        ["test-plan"] = Fixtures.Plan(),
    };

    private Task<AgentResult> Run(Dictionary<string, object> artifacts, string mode = "greenfield") =>
        new DocumenterAgent().RunAsync(Ctx.Make(_lib, _ws, artifacts: artifacts, mode: mode, title: "Add custom aliases"), CancellationToken.None);

    [Fact]
    public async Task WritesTheSixExpectedDocuments_AndReportsThem()
    {
        var r = await Run(Inputs());

        var docs = Ctx.Artifact<DocsReport>(r, "docs");
        var expected = new[] { "README.md", "docs/API.md", "docs/ARCHITECTURE.md", "docs/RISKS.md", "docs/ACCEPTANCE.md", "CHANGELOG.md" };
        Assert.Equal(expected, docs.Files.Select(f => f.Path));
        Assert.All(expected, p => Assert.True(_ws.Exists(p), p));
        Assert.All(docs.Files, f => Assert.Equal("create", f.Op));
        Assert.NotEmpty(r.Decisions);
    }

    [Fact]
    public async Task Readme_HasRunAndTestCommands_FeatureTitlesAndCurlExamples()
    {
        await Run(Inputs());
        var readme = _ws.ReadText("README.md");

        Assert.Contains("dotnet run --project src/UrlShortener.Api", readme);
        Assert.Contains("dotnet test tests/UrlShortener.Tests", readme);
        Assert.Contains("Create short links", readme);
        Assert.Contains("Redirect short links", readme);
        Assert.Contains("Custom aliases", readme);
        Assert.Contains("curl -i -X POST http://localhost:5000/api/links", readme);
        Assert.Contains("\"alias\":\"my-docs\"", readme);
        Assert.Contains("curl -i http://localhost:5000/abc1234", readme);
        Assert.Contains("A service that shortens URLs", readme);
        Assert.DoesNotContain("TODO", readme);
        Assert.DoesNotContain("{{", readme);
    }

    [Fact]
    public async Task OnlyInstalledFeaturesAreMentioned()
    {
        await Run(Inputs("shorten", "redirect", "alias"));

        var all = string.Join("\n", new[] { "README.md", "docs/API.md", "docs/ARCHITECTURE.md", "docs/RISKS.md", "docs/ACCEPTANCE.md", "CHANGELOG.md" }.Select(_ws.ReadText));
        Assert.DoesNotContain("Inspect and delete links", all);
        Assert.DoesNotContain("AC-MANAGE", all);
        Assert.DoesNotContain("DELETE", all);
        Assert.DoesNotContain("ttlSeconds", all);
        Assert.DoesNotContain("Link expiration", all);
        Assert.Contains("AC-ALIAS-1", _ws.ReadText("docs/ACCEPTANCE.md"));
    }

    [Fact]
    public async Task AddingAnInstalledFeature_AddsItsRoutesAndCriteria()
    {
        await Run(Inputs("shorten", "redirect", "manage"));

        Assert.Contains("DELETE", _ws.ReadText("docs/API.md"));
        Assert.Contains("AC-MANAGE-1", _ws.ReadText("docs/ACCEPTANCE.md"));
        Assert.Contains("curl -i -X DELETE http://localhost:5000/api/links/abc1234", _ws.ReadText("README.md"));
        Assert.DoesNotContain("Custom aliases", _ws.ReadText("README.md"));
    }

    [Fact]
    public async Task ApiDoc_RendersEndpointTablesFromOpenApiAndRequestFields()
    {
        await Run(Inputs());
        var api = _ws.ReadText("docs/API.md");

        Assert.Contains("| `POST` | `/api/links` | Create a short link | 201 Created, 400 Invalid request |", api);
        Assert.Contains("| `GET` | `/{code}` | Redirect to the original URL (302) | 302 Found, 404 Unknown code |", api);
        Assert.Contains("`alias`", api);
        Assert.Contains("string [A-Za-z0-9_-]{3,32} (optional)", api);
    }

    [Fact]
    public async Task ArchitectureDoc_RendersAdrsComponentsAndDataModel()
    {
        await Run(Inputs());
        var arch = _ws.ReadText("docs/ARCHITECTURE.md");

        Assert.Contains("### ADR-1: Vertical feature slices", arch);
        Assert.Contains("**Context.** Features change independently.", arch);
        Assert.Contains("**Decision.** Organise code as one folder per feature.", arch);
        Assert.Contains("- Layered architecture", arch);
        Assert.Contains("**Consequences.** Easy to add or remove features.", arch);
        Assert.Contains("HTTP host and composition root", arch);
        Assert.Contains("Link { code, url, createdAt }", arch);
        Assert.Contains("Redirects are fast", arch);
    }

    [Fact]
    public async Task RiskDoc_ListsRegisterColumns_AndSkipsRisksOfUninstalledFeatures()
    {
        var inputs = Inputs();
        var risks = Fixtures.Design().Risks.Append(new RiskItem("R-UNAUTH-DELETE", "Anyone can delete", "high", "medium", "Gateway", new(), "Accepted")).ToList();
        inputs["design"] = Fixtures.Design(risks);

        await Run(inputs);
        var doc = _ws.ReadText("docs/RISKS.md");

        Assert.Contains("| Id | Scenario | Likelihood | Impact | Mitigation | Validation | Residual |", doc);
        Assert.Contains("| R-COLLISION | Code collision | low | medium |", doc);
        Assert.Contains("AC-SHORT-2", doc);
        Assert.DoesNotContain("R-UNAUTH-DELETE", doc);
    }

    [Fact]
    public async Task Changelog_Greenfield_IsInitialReleaseWithFeatureList()
    {
        await Run(Inputs());
        var log = _ws.ReadText("CHANGELOG.md");
        Assert.Contains("Initial release", log);
        Assert.Contains("Custom aliases (`alias`)", log);
    }

    [Fact]
    public async Task Changelog_Brownfield_ComesFromImpact()
    {
        var inputs = Inputs();
        inputs["impact"] = new ImpactReport(new() { "shorten", "redirect" }, new(), new(), new(), new(), new() { "src/UrlShortener.Api/Features/Alias/AliasFeature.cs" },
            new() { "src/UrlShortener.Api/Core/UrlValidator.cs" }, new() { "POST /api/links gains optional field alias" }, new(),
            new() { new Finding("SEC-004", "block", "CWE-338", "src/UrlShortener.Api/Core/UrlValidator.cs", 12, "Weak randomness", "Use RandomNumberGenerator"),
                    new Finding("SEC-003", "block", "CWE-918", "src/UrlShortener.Api/Core/Other.cs", 3, "Outbound call", "Remove HttpClient") }, null);

        await Run(inputs, mode: "brownfield");
        var log = _ws.ReadText("CHANGELOG.md");

        Assert.DoesNotContain("Initial release", log);
        Assert.Contains("Add custom aliases", log);
        Assert.Contains("`src/UrlShortener.Api/Core/UrlValidator.cs`", log);
        Assert.Contains("`src/UrlShortener.Api/Features/Alias/AliasFeature.cs`", log);
        Assert.Contains("POST /api/links gains optional field alias", log);
        Assert.Contains("| SEC-004 | block | CWE-338 |", log);
        Assert.Contains("| fixed |", log);
        Assert.Contains("| open |", log);
    }

    [Fact]
    public async Task RerunWithSameInputs_ReportsUnchanged()
    {
        await Run(Inputs());
        var second = Ctx.Artifact<DocsReport>(await Run(Inputs()), "docs");
        Assert.All(second.Files, f => Assert.Equal("unchanged", f.Op));
    }

    [Fact]
    public void Autonomy_IsLimitedToDocumentationPaths()
    {
        var a = new DocumenterAgent();
        Assert.Equal(new[] { "README.md", "docs/**", "CHANGELOG.md" }, a.Autonomy.WriteGlobs);
        Assert.Equal("documenter", a.Name);
    }

    [Fact]
    public async Task MissingRequiredArtifact_Throws()
    {
        var inputs = Inputs();
        inputs.Remove("integration");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(inputs));
    }
}
