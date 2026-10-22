using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentic.Orchestrator;

namespace Agentic.Tests.Agents.Execution;

/// <summary>Unrestricted temp-directory workspace that journals the files changed by "the current node" (mirrors the real IWorkspace contract).</summary>
public sealed class TempWorkspace : IWorkspace, IDisposable
{
    private readonly List<FileChange> _changed = new();
    public string Root { get; }

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "agentic-test-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    private string Abs(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string rel) => File.Exists(Abs(rel));
    public string ReadText(string rel) => File.ReadAllText(Abs(rel));

    public IReadOnlyList<string> List(string glob = "**/*")
    {
        var rx = GlobToRegex(glob);
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(p => !p.StartsWith("bin/") && !p.StartsWith("obj/") && !p.Contains("/bin/") && !p.Contains("/obj/"))
            .Where(p => rx.IsMatch(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public FileChange WriteText(string rel, string content)
    {
        var op = Exists(rel) ? "modify" : "create";
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(rel))!);
        File.WriteAllText(Abs(rel), content, new UTF8Encoding(false));
        return Track(rel, op);
    }

    public FileChange CopyFrom(string absoluteSourcePath, string rel)
    {
        var op = Exists(rel) ? "modify" : "create";
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(rel))!);
        File.Copy(absoluteSourcePath, Abs(rel), overwrite: true);
        return Track(rel, op);
    }

    public void Remove(string rel)
    {
        var sha = Sha256(rel);
        var bytes = new FileInfo(Abs(rel)).Length;
        File.Delete(Abs(rel));
        _changed.Add(new FileChange(rel, "delete", sha, bytes));
    }

    public string Sha256(string rel) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Abs(rel)))).ToLowerInvariant();

    public void CopyTo(string absoluteDestDir)
    {
        foreach (var rel in List())
        {
            var dest = Path.Combine(absoluteDestDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Abs(rel), dest, overwrite: true);
        }
    }

    public IReadOnlyList<FileChange> ChangedFiles => _changed;

    /// <summary>Puts a file in place without journaling it (state that existed before the node ran).</summary>
    public TempWorkspace Seed(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(rel))!);
        File.WriteAllText(Abs(rel), content, new UTF8Encoding(false));
        return this;
    }

    public void ClearChanges() => _changed.Clear();

    private FileChange Track(string rel, string op)
    {
        var c = new FileChange(rel, op, Sha256(rel), new FileInfo(Abs(rel)).Length);
        _changed.Add(c);
        return c;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); } else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString());
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public sealed class FakeTestRunner : ITestRunner
{
    public List<(string Root, string Project)> Calls { get; } = new();
    public Func<string, TestReport> Respond { get; set; } = _ => Reports.Green();
    public Task<TestReport> RunAsync(string workspaceRoot, string testProjectRelPath, string? filter, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((workspaceRoot, testProjectRelPath));
        return Task.FromResult(Respond(workspaceRoot));
    }
}

public static class Reports
{
    public static TestReport Green(int total = 12) => new(total, 0, 0, total, 1500, 0, new(), "ok");
    public static TestReport Red(int failed = 2, int total = 12) => new(total - failed, failed, 0, total, 1500, 1, Enumerable.Range(1, failed).Select(i => $"Hardening.Test{i}").ToList(), "failed");
}

public sealed class StubSnapshot : IRunSnapshot
{
    private readonly JsonNode _metrics, _state, _lineage;
    public StubSnapshot(string metrics, string state, string lineage)
    {
        _metrics = JsonNode.Parse(metrics)!; _state = JsonNode.Parse(state)!; _lineage = JsonNode.Parse(lineage)!;
    }
    public JsonNode Metrics() => _metrics.DeepClone();
    public JsonNode State() => _state.DeepClone();
    public JsonNode Lineage() => _lineage.DeepClone();
    public JsonNode Audit() => new JsonObject();
}

/// <summary>A temp repo root with the REAL library/catalog.json and a few tiny fake slice files, so agent tests do not depend on the product being finished.</summary>
public sealed class FakeLibrary : IDisposable
{
    public string Root { get; }
    public FeatureCatalog Catalog { get; }

    public FakeLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "agentic-test-lib-" + Guid.NewGuid().ToString("N"));
        var real = FeatureCatalog.FindRepoRoot();
        Directory.CreateDirectory(Path.Combine(Root, "library"));
        File.Copy(Path.Combine(real, "library", "catalog.json"), Path.Combine(Root, "library", "catalog.json"));

        // core
        Add("src/UrlShortener.Api/Program.cs", "// host\n");
        Add("src/UrlShortener.Api/UrlShortener.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");
        Add("src/UrlShortener.Api/appsettings.json", "{}\n");
        Add("src/UrlShortener.Api/Core/Dummy.cs", "namespace UrlShortener.Api.Core; public static class Dummy { }\n");
        Add("src/UrlShortener.Api/Core/UrlValidator.cs", "namespace UrlShortener.Api.Core; public static class UrlValidator { public static bool Ok(string u) => u.StartsWith(\"http\"); }\n");
        Add("src/UrlShortener.Api/Core/CodeGenerator.cs", "namespace UrlShortener.Api.Core; public static class CodeGenerator { }\n");
        Add("tests/UrlShortener.Tests/UrlShortener.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Add("tests/UrlShortener.Tests/TestSupport/Support.cs", "namespace UrlShortener.Tests; public static class Support { }\n");
        Add("tests/UrlShortener.Tests/Core/HardeningTests.cs", Tests("Hardening", ("AC-HARD-1", "Rejects_JavaScript_Scheme"), ("AC-HARD-2", "Rejects_Private_Hosts"), ("AC-HARD-3", "Codes_Are_Not_Predictable")));

        // slices
        Slice("Shorten", ("AC-SHORT-1", "Creates_Link"), ("AC-SHORT-2", "Rejects_Invalid_Url"));
        Slice("Redirect", ("AC-REDIR-1", "Redirects"), ("AC-REDIR-2", "Unknown_Is_404"));
        Slice("Alias", ("AC-ALIAS-1", "Uses_Alias"));
        Slice("Manage", ("AC-MANAGE-1", "Gets_Metadata"));
        Catalog = FeatureCatalog.Load(Root);
    }

    public static string Tests(string cls, params (string Ac, string Method)[] tests) =>
        $"namespace UrlShortener.Tests;\npublic class {cls}Tests\n{{\n" +
        string.Concat(tests.Select(t => $"    [Fact]\n    [Trait(\"AC\", \"{t.Ac}\")]\n    public void {t.Method}() {{ }}\n\n")) + "}\n";

    private void Slice(string name, params (string Ac, string Method)[] tests)
    {
        Add($"src/UrlShortener.Api/Features/{name}/{name}Feature.cs", $"namespace UrlShortener.Api.Features.{name};\npublic sealed class {name}Feature : UrlShortener.Api.Core.IFeature {{ }}\n");
        Add($"src/UrlShortener.Api/Features/{name}/{name}Models.cs", $"namespace UrlShortener.Api.Features.{name};\npublic sealed record {name}Model(string Id);\n");
        Add($"tests/UrlShortener.Tests/Features/{name}/{name}Tests.cs", Tests(name, tests));
    }

    public void Add(string rel, string content)
    {
        var abs = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content, new UTF8Encoding(false));
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public static class Ctx
{
    public static AgentContext Make(FakeLibrary lib, IWorkspace ws, Dictionary<string, string>? p = null, Dictionary<string, object>? artifacts = null,
        ITestRunner? tests = null, IRunSnapshot? snapshot = null, string? runDir = null, string mode = "greenfield", string title = "Test scenario")
    {
        var nodes = (artifacts ?? new()).ToDictionary(kv => kv.Key, kv => Json.ToNode(kv.Value));
        return new AgentContext
        {
            Node = new NodeSpec { Id = "test-node", Agent = "test", Params = p ?? new() },
            Scenario = new ScenarioSpec { Id = "s1", Title = title, Mode = mode, RequirementText = "Build a URL shortener" },
            Workspace = ws,
            Catalog = lib.Catalog,
            Tests = tests ?? new FakeTestRunner(),
            Time = TimeProvider.System,
            Snapshot = snapshot,
            ArtifactReader = name => nodes.TryGetValue(name, out var n) ? n.DeepClone() : null,
            LogSink = _ => { },
            RunDirectory = runDir ?? Path.Combine(Path.GetTempPath(), "agentic-test-run-" + Guid.NewGuid().ToString("N")),
        };
    }

    public static T Artifact<T>(AgentResult r, string name) =>
        (r.Artifacts.TryGetValue(name, out var n) ? n : throw new InvalidOperationException($"artifact '{name}' missing; have: {string.Join(", ", r.Artifacts.Keys)}")).Deserialize<T>()!;

    public static Dictionary<string, string> P(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);
}

/// <summary>Canned artifacts used by the documenter / release manager / summariser tests.</summary>
public static class Fixtures
{
    public static RequirementsDoc Requirements(List<Ambiguity>? ambiguities = null) => new(
        "Build a URL shortener with custom aliases", "A service that shortens URLs and redirects visitors, with optional custom aliases.", new() { "visitor", "link owner" }, "clear",
        new()
        {
            new FeatureReq("shorten", "Create short links", "explicit", "Core ask"),
            new FeatureReq("redirect", "Redirect short links", "implied", "A short link is useless without redirect"),
            new FeatureReq("alias", "Custom aliases", "explicit", "Requested memorable links"),
        },
        new() { new Nfr("NFR-1", "performance", "Redirects are fast", "p95 < 50 ms", "implied") },
        ambiguities ?? new(), new() { "Links do not expire unless a TTL is given" }, new(), new(), 1);

    public static DesignDoc Design(List<RiskItem>? risks = null, string mode = "greenfield", params string[] features)
    {
        features = features.Length == 0 ? new[] { "shorten", "redirect", "alias" } : features;
        return new DesignDoc(mode, features.ToList(), features.ToList(), new(),
            new() { new ComponentInfo("api-host", "src/UrlShortener.Api/Program.cs", "HTTP host and composition root"), new ComponentInfo("core", "src/UrlShortener.Api/Core", "Shared primitives") },
            "Link { code, url, createdAt } held in an in-memory dictionary.",
            JsonNode.Parse("""
            {"openapi":"3.0.3","paths":{
              "/api/links":{"post":{"summary":"Create a short link","responses":{"201":{"description":"Created"},"400":{"description":"Invalid request"}}}},
              "/{code}":{"get":{"summary":"Redirect to the original URL (302)","responses":{"302":{"description":"Found"},"404":{"description":"Unknown code"}}}}}}
            """)!,
            new() { new Adr("ADR-1", "Vertical feature slices", "accepted", "Features change independently.", "Organise code as one folder per feature.", new() { "Layered architecture" }, "Easy to add or remove features.") },
            risks ?? new()
            {
                new RiskItem("R-COLLISION", "Code collision", "low", "medium", "Large code space and atomic create", new() { "AC-SHORT-2" }, "Low"),
                new RiskItem("R-ALIAS-SQUAT", "Alias squatting", "medium", "medium", "Reserved words", new() { "AC-ALIAS-1" }, "Accepted: brand squatting needs moderation"),
            },
            new()
            {
                new TaskSpec("implement:shorten", "shorten", "slice", new(), new(), false, null),
                new TaskSpec("implement:alias", "alias", "slice", new() { "implement:shorten" }, new(), false, null),
            });
    }

    public static IntegrationReport Integration(params string[] features) =>
        new((features.Length == 0 ? new[] { "shorten", "redirect", "alias" } : features).ToList(), "src/UrlShortener.Api/Features/FeatureRegistry.cs", new());

    public static TestPlan Plan(params string[] required) =>
        new(new() { "unit", "integration" },
            new()
            {
                ["shorten"] = new() { new AcceptanceCriterion("AC-SHORT-1", "Create returns 201"), new AcceptanceCriterion("AC-SHORT-2", "Invalid rejected") },
                ["redirect"] = new() { new AcceptanceCriterion("AC-REDIR-1", "Redirect 302") },
                ["alias"] = new() { new AcceptanceCriterion("AC-ALIAS-1", "Alias used") },
                ["manage"] = new() { new AcceptanceCriterion("AC-MANAGE-1", "Metadata") },
            },
            required.Length == 0 ? new() { "AC-SHORT-1", "AC-SHORT-2", "AC-REDIR-1", "AC-ALIAS-1" } : required.ToList(), new() { "tests-green", "coverage-100" });

    public static Traceability Trace(double coverage = 1.0, params string[] missing) =>
        new(new()
        {
            new TraceRow("AC-SHORT-2", "shorten", missing.Contains("AC-SHORT-2") ? new() : new() { "t.cs::A" }, !missing.Contains("AC-SHORT-2")),
            new TraceRow("AC-ALIAS-1", "alias", missing.Contains("AC-ALIAS-1") ? new() : new() { "t.cs::B" }, !missing.Contains("AC-ALIAS-1")),
        }, coverage, missing.ToList());

    public static DocsReport Docs() => new(new[] { "README.md", "docs/API.md", "docs/ARCHITECTURE.md", "docs/RISKS.md" }.Select(p => new FileChange(p, "create", "abc", 10)).ToList());

    public static SecurityReport CleanSecurity() => new(10, new(), true);

    /// <summary>Everything a release needs to be GO.</summary>
    public static Dictionary<string, object> GoArtifacts() => new()
    {
        ["requirements"] = Requirements(),
        ["design"] = Design(),
        ["integration"] = Integration(),
        ["test-report"] = Reports.Green(),
        ["traceability"] = Trace(),
        ["security-report"] = CleanSecurity(),
        ["docs"] = Docs(),
    };

    public const string ChangesState = """
    {"status":"Running","changes":[
      {"path":"src/UrlShortener.Api/Features/Shorten/ShortenFeature.cs","op":"create","nodeId":"implement:shorten"},
      {"path":"src/UrlShortener.Api/Features/Alias/AliasFeature.cs","op":"create","nodeId":"implement:alias"},
      {"path":"src/UrlShortener.Api/Core/UrlValidator.cs","op":"modify","nodeId":"implement:hardening"},
      {"path":"src/UrlShortener.Api/Features/FeatureRegistry.cs","op":"create","nodeId":"integrate"},
      {"path":"src/UrlShortener.Api/Features/FeatureRegistry.cs","op":"modify","nodeId":"integrate"}]}
    """;

    public static StubSnapshot Snapshot(string? state = null) => new("""{"successRate":1}""", state ?? ChangesState, "[]");
}
