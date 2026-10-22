namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Derives the test plan from the design: every acceptance criterion of every feature in the full target set must be traceable to a test,
/// because the traceability gate checks the final workspace (existing and new features alike).
/// </summary>
public sealed class TestPlannerAgent : IAgent
{
    public const string AgentName = "test-planner";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy { get; } = new(Array.Empty<string>());

    public Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        var design = ctx.Read<DesignDoc>("design");
        var result = new AgentResult();

        var byFeature = new Dictionary<string, List<AcceptanceCriterion>>(StringComparer.Ordinal);
        foreach (var id in design.Features)
            if (ctx.Catalog.Features.TryGetValue(id, out var def)) byFeature[id] = def.Acceptance.ToList();
        var required = byFeature.Values.SelectMany(l => l).Select(a => a.Id).Distinct().ToList();

        var levels = new List<string>
        {
            "unit",
            "integration (WebApplicationFactory, in-process HTTP)",
            "policy/security scan",
            "traceability (every acceptance criterion has a test)",
        };
        var brownfield = design.Mode == "brownfield";
        if (brownfield) levels.Add("regression (existing baseline suite must stay green)");

        var plan = new TestPlan(levels, byFeature, required, new() { "tests-green", "traceability-complete", "security-passed", "release-go" });
        result.Add("test-plan", plan);
        result.Decide("Require acceptance criteria of the full target feature set",
            $"{required.Count} criteria across {byFeature.Count} feature(s) must each be covered by a test; traceability is evaluated on the final workspace" +
            (brownfield ? ", so existing baseline features are included and a regression level guards them." : "."),
            new[] { "Only require criteria of newly installed features" },
            "Stricter gate and slightly more test authoring, but a regression in an existing feature cannot slip through.");
        return Task.FromResult(result);
    }
}
