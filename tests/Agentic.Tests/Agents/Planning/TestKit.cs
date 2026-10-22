using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentic.Orchestrator;

namespace Agentic.Tests.Agents.Planning;

/// <summary>Unrestricted temp-dir workspace used by the planning-agent tests.</summary>
public sealed class TempWorkspace : IWorkspace, IDisposable
{
    public string Root { get; }
    public TempWorkspace() { Root = Path.Combine(Path.GetTempPath(), "agentic-planning-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }

    public TempWorkspace With(string rel, string content) { WriteText(rel, content); return this; }

    public bool Exists(string rel) => File.Exists(Abs(rel));
    public string ReadText(string rel) => File.ReadAllText(Abs(rel));
    public IReadOnlyList<string> List(string glob = "**/*")
    {
        var rx = new Regex("^" + Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$");
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(p => !p.StartsWith("bin/") && !p.StartsWith("obj/") && !p.Contains("/bin/") && !p.Contains("/obj/") && rx.IsMatch(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
    public FileChange WriteText(string rel, string content)
    {
        var abs = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return new FileChange(rel, "create", Sha256(rel), content.Length);
    }
    public FileChange CopyFrom(string absoluteSourcePath, string rel) => WriteText(rel, File.ReadAllText(absoluteSourcePath));
    public void Remove(string rel) { if (Exists(rel)) File.Delete(Abs(rel)); }
    public string Sha256(string rel) => Json.Sha256Hex(ReadText(rel));
    public void CopyTo(string absoluteDestDir) => throw new NotSupportedException();
    public IReadOnlyList<FileChange> ChangedFiles => Array.Empty<FileChange>();
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    private string Abs(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
}

public sealed class FakeTestRunner : ITestRunner
{
    public TestReport Report { get; set; } = new(12, 0, 0, 12, 100, 0, new(), "");
    public List<(string root, string project)> Calls { get; } = new();
    public Task<TestReport> RunAsync(string workspaceRoot, string testProjectRelPath, string? filter, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((workspaceRoot, testProjectRelPath));
        return Task.FromResult(Report);
    }
}

public static class Kit
{
    public static readonly FeatureCatalog Catalog = FeatureCatalog.Load();

    public const string AmbiguousText = "Make the link shortener better for our marketing team. It should be fast and secure, scale well, and give us some reporting on how campaigns are doing.";
    public const string ClearText = "Create short links with custom aliases and link expiry, track clicks, and rate limit abuse; provide health checks";

    public static ScenarioSpec Scenario(string text, string workspace = "empty", string mode = "greenfield") =>
        new() { Id = "t", Title = "t", Mode = mode, RequirementText = text, Workspace = workspace };

    public static AgentContext Ctx(ScenarioSpec scenario, IWorkspace? ws = null, Dictionary<string, JsonNode>? artifacts = null,
        Dictionary<string, string>? p = null, ITestRunner? runner = null, string nodeId = "node")
    {
        artifacts ??= new();
        return new AgentContext
        {
            Node = new NodeSpec { Id = nodeId, Agent = "test", Params = p ?? new() },
            Scenario = scenario,
            Workspace = ws ?? new TempWorkspace(),
            Catalog = Catalog,
            Tests = runner ?? new FakeTestRunner(),
            Time = TimeProvider.System,
            ArtifactReader = n => artifacts.TryGetValue(n, out var v) ? v : null,
            LogSink = _ => { },
            RunDirectory = Path.GetTempPath(),
        };
    }

    public static async Task<AgentResult> Run(IAgent agent, AgentContext ctx) => await agent.RunAsync(ctx, CancellationToken.None);

    public static void Publish(Dictionary<string, JsonNode> store, AgentResult r) { foreach (var (k, v) in r.Artifacts) store[k] = v.DeepClone(); }

    public static T Get<T>(AgentResult r, string name) => r.Artifacts[name].Deserialize<T>()!;

    public static Dictionary<string, string> Answers(Dictionary<string, string> answers) => new() { ["answers"] = Json.Serialize(answers) };

    public static readonly Dictionary<string, string> HighAnswers = new()
    {
        ["AMB-PERFORMANCE"] = "p95 redirect latency under 50 ms",
        ["AMB-SECURITY"] = "rate limit link creation and block dangerous URLs",
        ["AMB-REPORTING"] = "daily click counts per link with referrers",
    };

    public static async Task<RequirementsDoc> Requirements(string text, string workspace = "empty", Dictionary<string, string>? p = null)
    {
        var r = await Run(new Agentic.Orchestrator.Agents.RequirementsAgent(), Ctx(Scenario(text, workspace), p: p));
        return Get<RequirementsDoc>(r, "requirements");
    }
}
