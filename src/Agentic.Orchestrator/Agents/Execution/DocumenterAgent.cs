using System.Text;
using System.Text.Json.Nodes;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Generates the project documentation of the generated workspace (README, API, architecture, risks, acceptance criteria, changelog)
/// purely from run artifacts and the feature catalog – nothing is free-written, and only installed features are mentioned.
/// </summary>
public sealed class DocumenterAgent : IAgent
{
    public const string AgentName = "documenter";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => new(new[] { "README.md", "docs/**", "CHANGELOG.md" });

    private const string BaseUrl = "http://localhost:5000";
    private static readonly string[] HttpMethods = { "get", "post", "put", "patch", "delete", "head", "options" };

    private sealed record Endpoint(string Method, string Path, string Summary, string Responses);

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var requirements = ctx.Read<RequirementsDoc>("requirements");
        var design = ctx.Read<DesignDoc>("design");
        var integration = ctx.Read<IntegrationReport>("integration");
        var impact = ctx.ReadOrDefault<ImpactReport>("impact");
        var plan = ctx.ReadOrDefault<TestPlan>("test-plan");
        var cat = ctx.Catalog;

        // Only installed features are documented.
        var installedIds = integration.InstalledFeatures.Where(cat.Features.ContainsKey).ToList();
        var installed = installedIds.Select(cat.Get).ToList();
        var installedFeatures = installed.Where(f => !f.IsPatch).ToList();
        var notInstalled = cat.Features.Values.Where(f => !installedIds.Contains(f.Id)).ToList();

        var files = new List<FileChange>
        {
            ExecutionSupport.WriteTracked(ctx.Workspace, "README.md", Readme(requirements, installed, installedFeatures, cat)),
            ExecutionSupport.WriteTracked(ctx.Workspace, "docs/API.md", Api(design, installedFeatures, notInstalled, cat)),
            ExecutionSupport.WriteTracked(ctx.Workspace, "docs/ARCHITECTURE.md", Architecture(requirements, design, installed, cat)),
            ExecutionSupport.WriteTracked(ctx.Workspace, "docs/RISKS.md", Risks(design, notInstalled)),
            ExecutionSupport.WriteTracked(ctx.Workspace, "docs/ACCEPTANCE.md", Acceptance(plan, installed, cat)),
            ExecutionSupport.WriteTracked(ctx.Workspace, "CHANGELOG.md", Changelog(impact, installed, ctx.Scenario)),
        };
        ctx.Log($"documenter: wrote {files.Count} file(s) for {installedIds.Count} installed feature(s)");

        return new AgentResult()
            .Add("docs", new DocsReport(files))
            .Decide("Generate documentation from run artifacts",
                $"Rendered {files.Count} documents from the requirements, design, integration{(impact is not null ? ", impact" : "")}{(plan is not null ? " and test-plan" : "")} artifacts. Only the {installedIds.Count} installed feature(s) are documented ({string.Join(", ", installedIds)}).",
                new[] { "Hand-written prose (drifts from the code)", "Generate OpenAPI JSON only (not readable for reviewers)" },
                "Generated docs are always consistent with what was built, but are only as rich as the structured artifacts they come from.");
    }

    // ───────────── README ─────────────

    private static string Readme(RequirementsDoc req, List<FeatureDef> installed, List<FeatureDef> features, FeatureCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# URL Shortener").AppendLine();
        sb.AppendLine($"> {Single(req.ProblemStatement)}").AppendLine();
        sb.AppendLine("A small HTTP service (ASP.NET Core minimal API, .NET 8, in-memory link store) that turns long URLs into short links. It is assembled from vertical feature slices, each with its own tests.").AppendLine();

        sb.AppendLine("## Features").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Feature", "Id", "Endpoints / fields" },
            installed.Select(f => new[] { f.Title, f.Id, EndpointSummary(f) }))).AppendLine();

        sb.AppendLine("## Run and test").AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine($"dotnet run --project src/UrlShortener.Api --urls {BaseUrl}");
        sb.AppendLine("dotnet test tests/UrlShortener.Tests");
        sb.AppendLine("```").AppendLine();

        sb.AppendLine("## Usage examples").AppendLine();
        sb.AppendLine("All examples assume the service listens on `" + BaseUrl + "`.").AppendLine();
        foreach (var f in features)
        {
            var examples = CurlExamples(f);
            if (examples.Count == 0) continue;
            sb.AppendLine($"### {f.Title}").AppendLine();
            sb.AppendLine("```bash");
            foreach (var e in examples) sb.AppendLine(e);
            sb.AppendLine("```").AppendLine();
        }
        foreach (var patch in installed.Where(f => f.IsPatch))
        {
            sb.AppendLine($"### {patch.Title}").AppendLine();
            sb.Append(ExecutionSupport.Bullets(patch.Acceptance.Select(a => a.Text))).AppendLine();
        }

        sb.AppendLine("## Documentation").AppendLine();
        sb.AppendLine("- [API reference](docs/API.md)");
        sb.AppendLine("- [Architecture and decisions](docs/ARCHITECTURE.md)");
        sb.AppendLine("- [Risk register](docs/RISKS.md)");
        sb.AppendLine("- [Acceptance criteria](docs/ACCEPTANCE.md)");
        sb.AppendLine("- [Changelog](CHANGELOG.md)").AppendLine();

        if (req.Assumptions.Count > 0)
        {
            sb.AppendLine("## Assumptions").AppendLine();
            sb.Append(ExecutionSupport.Bullets(req.Assumptions)).AppendLine();
        }
        return sb.ToString();
    }

    private static string EndpointSummary(FeatureDef f)
    {
        var parts = f.Routes.Select(r => $"`{r.Method} {Brace(r.Path)}`").ToList();
        parts.AddRange(f.RequestFields.Select(r => $"`{r.Field}` on `{r.Route}`"));
        return parts.Count == 0 ? (f.IsPatch ? "hardening of existing endpoints" : "behaviour only") : string.Join(", ", parts);
    }

    private static List<string> CurlExamples(FeatureDef f)
    {
        var list = new List<string>();
        foreach (var r in f.Routes)
        {
            var path = r.Path.Replace(":code", "abc1234");
            if (r.Method == "POST" && r.Request is { Count: > 0 })
            {
                var body = string.Join(",", r.Request.Keys.Select(k => $"\"{k}\":{JsonExample(k, r.Request[k])}"));
                list.Add($"# {r.Summary}");
                list.Add($"curl -i -X POST {BaseUrl}{path} -H 'Content-Type: application/json' -d '{{{body}}}'");
            }
            else
            {
                list.Add($"# {r.Summary}");
                list.Add(r.Method == "GET" ? $"curl -i {BaseUrl}{path}" : $"curl -i -X {r.Method} {BaseUrl}{path}");
            }
        }
        foreach (var rf in f.RequestFields)
        {
            var isHeader = rf.Field.Contains("(header)", StringComparison.OrdinalIgnoreCase);
            var name = rf.Field.Replace("(header)", "", StringComparison.OrdinalIgnoreCase).Trim();
            var route = rf.Route.Split(' ', 2);
            var path = route.Length == 2 ? route[1] : rf.Route;
            list.Add($"# {name} ({rf.Type})");
            list.Add(isHeader
                ? $"curl -i -X POST {BaseUrl}{path} -H 'Content-Type: application/json' -H '{name}: 7d2c9a1e-5b1f-4e53-9a0c-1f6f7e0a1b11' -d '{{\"url\":\"https://example.com/docs\"}}'"
                : $"curl -i -X POST {BaseUrl}{path} -H 'Content-Type: application/json' -d '{{\"url\":\"https://example.com/docs\",\"{name}\":{JsonExample(name, rf.Type)}}}'");
        }
        return list;
    }

    private static string JsonExample(string field, string type)
    {
        if (field == "url") return "\"https://example.com/docs\"";
        if (field == "alias") return "\"my-docs\"";
        if (field == "ttlSeconds") return "3600";
        return type.TrimStart().StartsWith("integer", StringComparison.OrdinalIgnoreCase) ? "60" : "\"example\"";
    }

    // ───────────── API.md ─────────────

    private static string Api(DesignDoc design, List<FeatureDef> features, List<FeatureDef> notInstalled, FeatureCatalog cat)
    {
        var endpoints = ParseOpenApi(design.OpenApi);
        // Drop endpoints that only belong to features that are not part of this build.
        var foreign = notInstalled.SelectMany(f => f.Routes).Select(r => Key(r.Method, r.Path)).ToHashSet();
        var own = features.SelectMany(f => f.Routes).Select(r => Key(r.Method, r.Path)).ToHashSet();
        endpoints = endpoints.Where(e => !(foreign.Contains(Key(e.Method, e.Path)) && !own.Contains(Key(e.Method, e.Path)))).ToList();
        if (endpoints.Count == 0)
            endpoints = features.SelectMany(f => f.Routes.Select(r => new Endpoint(r.Method, Brace(r.Path), r.Summary,
                string.Join(", ", (r.Responses ?? new()).Select(kv => $"{kv.Key} {kv.Value}"))))).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# API reference").AppendLine();
        sb.AppendLine($"Base URL (local): `{BaseUrl}`. Bodies are JSON (`Content-Type: application/json`); errors are JSON objects with a machine-readable `error` code.").AppendLine();
        sb.AppendLine("## Endpoints").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Method", "Path", "Summary", "Responses" },
            endpoints.Select(e => new[] { $"`{e.Method}`", $"`{e.Path}`", e.Summary, e.Responses }))).AppendLine();

        sb.AppendLine("## Request fields").AppendLine();
        var fieldRows = new List<string[]>();
        foreach (var f in features)
        {
            foreach (var r in f.Routes.Where(r => r.Request is not null))
                foreach (var (field, type) in r.Request!) fieldRows.Add(new[] { $"`{r.Method} {Brace(r.Path)}`", $"`{field}`", type, f.Title });
            foreach (var rf in f.RequestFields) fieldRows.Add(new[] { $"`{rf.Route}`", $"`{rf.Field}`", rf.Type, f.Title });
        }
        sb.Append(ExecutionSupport.Table(new[] { "Endpoint", "Field", "Type", "Provided by" }, fieldRows)).AppendLine();

        sb.AppendLine("## Per-endpoint responses").AppendLine();
        foreach (var f in features.Where(f => f.Routes.Count > 0))
        {
            sb.AppendLine($"### {f.Title}").AppendLine();
            foreach (var r in f.Routes)
            {
                sb.AppendLine($"**`{r.Method} {Brace(r.Path)}`** – {r.Summary}").AppendLine();
                sb.Append(ExecutionSupport.Table(new[] { "Status", "Meaning" }, (r.Responses ?? new()).Select(kv => new[] { kv.Key, kv.Value }))).AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Key(string method, string path) => method.ToUpperInvariant() + " " + Brace(path);
    private static string Brace(string path) => System.Text.RegularExpressions.Regex.Replace(path, @":(\w+)", "{$1}");

    private static List<Endpoint> ParseOpenApi(JsonNode? openApi)
    {
        var list = new List<Endpoint>();
        var paths = JsonView.Prop(openApi, "paths");
        if (paths is JsonObject po)
        {
            foreach (var (path, item) in po)
                if (item is JsonObject io)
                    foreach (var (method, op) in io)
                        if (op is not null && HttpMethods.Contains(method.ToLowerInvariant()))
                            list.Add(new Endpoint(method.ToUpperInvariant(), Brace(path), JsonView.Str(op, "summary", "description"), ResponsesText(JsonView.Prop(op, "responses"))));
        }
        else if (paths is JsonArray pa)
        {
            foreach (var item in pa)
                list.Add(new Endpoint(JsonView.Str(item, "method").ToUpperInvariant(), Brace(JsonView.Str(item, "path")), JsonView.Str(item, "summary"), ResponsesText(JsonView.Prop(item, "responses"))));
        }
        return list.Where(e => e.Method.Length > 0 && e.Path.Length > 0).ToList();
    }

    private static string ResponsesText(JsonNode? responses)
    {
        var parts = new List<string>();
        foreach (var (key, value) in JsonView.Entries(responses))
            parts.Add(value is JsonValue ? $"{key} {JsonView.Text(value)}" : $"{key} {JsonView.Str(value, "description")}".Trim());
        return string.Join(", ", parts);
    }

    // ───────────── ARCHITECTURE.md ─────────────

    private static string Architecture(RequirementsDoc req, DesignDoc design, List<FeatureDef> installed, FeatureCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Architecture").AppendLine();
        sb.AppendLine($"Mode: **{design.Mode}**. Features in this build: {string.Join(", ", installed.Select(f => $"`{f.Id}`"))}.").AppendLine();

        sb.AppendLine("## Components").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Component", "Path", "Responsibility" }, design.Components.Select(c => new[] { c.Id, $"`{c.Path}`", c.Responsibility }))).AppendLine();

        var hooks = installed.Where(f => f.Hooks is not null && ((f.Hooks.Provides?.Count ?? 0) > 0 || (f.Hooks.Uses?.Count ?? 0) > 0)).ToList();
        if (hooks.Count > 0)
        {
            sb.AppendLine("## Extension hooks").AppendLine();
            sb.AppendLine("Feature slices compose through hooks instead of calling each other directly.").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Feature", "Provides", "Uses" },
                hooks.Select(f => new[] { f.Id, string.Join(", ", f.Hooks!.Provides ?? new()), string.Join(", ", f.Hooks.Uses ?? new()) }))).AppendLine();
        }

        sb.AppendLine("## Data model").AppendLine();
        sb.AppendLine(design.DataModel.Trim()).AppendLine();

        if (req.Nfrs.Count > 0)
        {
            sb.AppendLine("## Quality attributes").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Id", "Category", "Statement", "Target" }, req.Nfrs.Select(n => new[] { n.Id, n.Category, n.Statement, n.Target ?? "" }))).AppendLine();
        }

        sb.AppendLine("## Architecture decisions").AppendLine();
        if (design.Adrs.Count == 0) sb.AppendLine("_No decisions recorded._").AppendLine();
        foreach (var adr in design.Adrs)
        {
            sb.AppendLine($"### {adr.Id}: {adr.Title}").AppendLine();
            sb.AppendLine($"**Status:** {adr.Status}").AppendLine();
            sb.AppendLine($"**Context.** {adr.Context}").AppendLine();
            sb.AppendLine($"**Decision.** {adr.Decision}").AppendLine();
            sb.AppendLine("**Alternatives considered:**").AppendLine();
            sb.Append(ExecutionSupport.Bullets(adr.Alternatives)).AppendLine();
            sb.AppendLine($"**Consequences.** {adr.Consequences}").AppendLine();
        }
        return sb.ToString();
    }

    // ───────────── RISKS.md ─────────────

    private static string Risks(DesignDoc design, List<FeatureDef> notInstalled)
    {
        var foreignRisks = notInstalled.SelectMany(f => f.Risks).Select(r => r.Id).ToHashSet();
        var risks = design.Risks.Where(r => !foreignRisks.Contains(r.Id)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Risk register").AppendLine();
        sb.AppendLine("Each risk lists its mitigation, the acceptance criteria that validate the mitigation and the residual risk after mitigation. An empty validation column means the risk is accepted by documentation rather than by a test.").AppendLine();
        sb.Append(ExecutionSupport.Table(new[] { "Id", "Scenario", "Likelihood", "Impact", "Mitigation", "Validation", "Residual" },
            risks.Select(r => new[] { r.Id, r.Scenario, r.Likelihood, r.Impact, r.Mitigation, r.Validation.Count == 0 ? "-" : string.Join(", ", r.Validation), r.Residual }))).AppendLine();
        return sb.ToString();
    }

    // ───────────── ACCEPTANCE.md ─────────────

    private static string Acceptance(TestPlan? plan, List<FeatureDef> installed, FeatureCatalog cat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Acceptance criteria").AppendLine();
        if (plan is not null)
        {
            sb.AppendLine($"Test levels: {string.Join(", ", plan.Levels)}. Quality gates: {string.Join(", ", plan.Gates)}.").AppendLine();
            sb.AppendLine("Tests reference these ids through `[Trait(\"AC\", \"<id>\")]`.").AppendLine();
        }
        foreach (var f in installed)
        {
            var criteria = plan is not null && plan.AcceptanceByFeature.TryGetValue(f.Id, out var planned) ? planned : f.Acceptance;
            if (criteria.Count == 0) continue;
            sb.AppendLine($"## {f.Title} (`{f.Id}`)").AppendLine();
            sb.Append(ExecutionSupport.Table(new[] { "Id", "Criterion" }, criteria.Select(c => new[] { c.Id, c.Text }))).AppendLine();
        }
        return sb.ToString();
    }

    // ───────────── CHANGELOG.md ─────────────

    private static string Changelog(ImpactReport? impact, List<FeatureDef> installed, ScenarioSpec scenario)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Changelog").AppendLine();
        if (impact is null)
        {
            sb.AppendLine("## 1.0.0 - Initial release").AppendLine();
            sb.AppendLine("First release of the URL shortener.").AppendLine();
            sb.AppendLine("### Features").AppendLine();
            sb.Append(ExecutionSupport.Bullets(installed.Select(f => $"{f.Title} (`{f.Id}`)"))).AppendLine();
            return sb.ToString();
        }

        sb.AppendLine($"## 1.1.0 - {scenario.Title}").AppendLine();
        sb.AppendLine($"Change set applied on top of the existing baseline (features present before: {string.Join(", ", impact.InstalledFeatures)}).").AppendLine();
        sb.AppendLine("### Modified files").AppendLine();
        sb.Append(ExecutionSupport.Bullets(impact.ModifiedFiles.Select(p => $"`{p}`"))).AppendLine();
        sb.AppendLine("### New files").AppendLine();
        sb.Append(ExecutionSupport.Bullets(impact.NewFiles.Select(p => $"`{p}`"))).AppendLine();
        sb.AppendLine("### API changes").AppendLine();
        sb.Append(ExecutionSupport.Bullets(impact.ApiChanges, "_No API changes._")).AppendLine();
        sb.AppendLine("### Findings addressed").AppendLine();
        var modified = impact.ModifiedFiles.Select(p => p.Replace('\\', '/')).ToHashSet();
        sb.Append(ExecutionSupport.Table(new[] { "Finding", "Severity", "CWE", "Location", "Description", "Fix", "Status" },
            impact.Findings.Select(f => new[] { f.Id, f.Severity, f.Cwe ?? "-", $"`{f.File}:{f.Line}`", f.Description, f.Fix, modified.Contains(f.File.Replace('\\', '/')) ? "fixed" : "open" }))).AppendLine();
        return sb.ToString();
    }

    private static string Single(string s) => string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
