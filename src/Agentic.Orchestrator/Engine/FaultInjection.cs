namespace Agentic.Orchestrator;

public sealed class InjectedFaultException(string message) : Exception(message);

/// <summary>
/// Chaos wrapper used by scenarios/tests to prove the resilience controls. Faults are keyed by (node, agent, attempt) so a
/// fallback agent is not hit by a fault aimed at the primary one.
/// Modes: throw (agent crashes) | bad-output (agent leaves a syntactically broken file → syntax gate fails → rollback) |
/// critical-policy (agent leaks a secret → immediate safe-stop).
/// </summary>
public sealed class FaultInjectingAgent(IAgent inner, IReadOnlyList<FaultSpec> faults) : IAgent
{
    public string Name => inner.Name;
    public AutonomyBoundary Autonomy => inner.Autonomy;

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        var fault = faults.FirstOrDefault(f => f.Node == ctx.Node.Id && f.Agent == inner.Name && f.Attempts.Contains(ctx.Attempt));
        if (fault is null) return await inner.RunAsync(ctx, ct);

        ctx.Log($"CHAOS: injecting '{fault.Mode}' into {ctx.Node.Id} (agent {inner.Name}, attempt {ctx.Attempt})");
        switch (fault.Mode)
        {
            case "throw":
                throw new InjectedFaultException($"Injected fault: agent '{inner.Name}' crashed on attempt {ctx.Attempt}");
            case "bad-output":
            {
                var result = await inner.RunAsync(ctx, ct);
                ctx.Workspace.WriteText("src/UrlShortener.Api/Core/__InjectedDefect.cs", "namespace Broken { public class { int x = ; } ");
                return result;
            }
            case "critical-policy":
            {
                var result = await inner.RunAsync(ctx, ct);
                ctx.Workspace.WriteText("src/UrlShortener.Api/Core/__InjectedLeak.cs",
                    "namespace Leak { public static class K { public const string Key = \"AKIAIOSFODNN7EXAMPLE\"; } }");
                return result;
            }
            default:
                throw new InvalidOperationException($"Unknown fault mode '{fault.Mode}'.");
        }
    }
}

/// <summary>Name → agent lookup. Wraps agents with the fault injector when the scenario defines faults.</summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);

    public AgentRegistry(IEnumerable<IAgent> agents, IReadOnlyList<FaultSpec>? faults = null)
    {
        foreach (var a in agents) _agents[a.Name] = faults is { Count: > 0 } ? new FaultInjectingAgent(a, faults) : a;
    }

    public IAgent Get(string name) =>
        _agents.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No agent registered with name '{name}'.");
    public bool Has(string name) => _agents.ContainsKey(name);
    public IEnumerable<string> Names => _agents.Keys;
}
