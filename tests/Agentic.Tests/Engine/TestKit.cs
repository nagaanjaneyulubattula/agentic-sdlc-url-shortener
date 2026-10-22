using Agentic.Orchestrator;

namespace Agentic.Tests.EngineTests;

public sealed class LambdaAgent(string name, string[] writeGlobs, Func<AgentContext, CancellationToken, Task<AgentResult>> run) : IAgent
{
    public LambdaAgent(string name, string[] writeGlobs, Func<AgentContext, Task<AgentResult>> run) : this(name, writeGlobs, (c, _) => run(c)) { }
    public string Name => name;
    public AutonomyBoundary Autonomy => new(writeGlobs);
    public Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct) => run(ctx, ct);
}

public sealed class FuncApprovals(Func<ApprovalRequest, ApprovalDecision> decide) : IApprovalProvider
{
    public List<ApprovalRequest> Requests { get; } = new();
    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(decide(request));
    }
}

public sealed class StubTestRunner : ITestRunner
{
    public Task<TestReport> RunAsync(string workspaceRoot, string testProjectRelPath, string? filter, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(new TestReport(1, 0, 0, 1, 1, 0, new(), ""));
}

public static class Kit
{
    private static readonly Lazy<FeatureCatalog> _catalog = new(() => FeatureCatalog.Load());
    public static FeatureCatalog Catalog => _catalog.Value;

    public static ScenarioSpec Scenario(string id = "t", string workspace = "empty") =>
        new() { Id = id, Title = id, Mode = "greenfield", RequirementText = "test", Workspace = workspace };

    public static NodeSpec Node(string id, string agent, params string[] deps) =>
        new() { Id = id, Agent = agent, Stage = "test", Deps = deps.ToList(), Retry = new RetryPolicy(0, 0), Rollback = true };

    public static AgentResult Ok(params (string name, object value)[] artifacts)
    {
        var r = new AgentResult();
        foreach (var (n, v) in artifacts) r.Add(n, v);
        return r;
    }

    public static Engine Create(ScenarioSpec scenario, IEnumerable<NodeSpec> plan, IEnumerable<IAgent> agents,
        IApprovalProvider? approvals = null, Action<EngineOptions>? tweak = null)
    {
        var o = new EngineOptions
        {
            RunsRoot = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N")),
            TestRunner = new StubTestRunner(),
            Approvals = approvals ?? new FuncApprovals(_ => new ApprovalDecision(true, "test-approver", "ok")),
            Plan = _ => plan.ToList(),
            Delay = (_, _) => Task.CompletedTask,
        };
        tweak?.Invoke(o);
        return new Engine(scenario, Catalog, agents, o);
    }

    public static LambdaAgent Agent(string name, Func<AgentContext, Task<AgentResult>> run, params string[] writeGlobs) =>
        new(name, writeGlobs, run);

    public static LambdaAgent AgentCt(string name, Func<AgentContext, CancellationToken, Task<AgentResult>> run, params string[] writeGlobs) =>
        new(name, writeGlobs, run);

    public static LambdaAgent Sync(string name, Func<AgentContext, AgentResult> run, params string[] writeGlobs) =>
        new(name, writeGlobs, ctx => Task.FromResult(run(ctx)));

    public static RequirementsDoc Requirements(string status, IEnumerable<string> features, bool answered = false) =>
        new("raw", "problem", new() { "client" }, status,
            features.Select(f => new FeatureReq(f, f, "explicit", "test")).ToList(), new(),
            new() { new Ambiguity("AMB-X", "fast", "fast", "high", "How fast?", "50ms", answered, answered ? "50ms" : null) },
            new(), answered ? new() : new() { new OpenQuestion("AMB-X", "How fast?") }, new(), 1);

    public static NodeState N(Engine e, string id) => e.State.Nodes[id];
}
