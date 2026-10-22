namespace Agentic.Orchestrator.Agents;

/// <summary>The default agent roster: planning agents (requirements, analyst, architect, test planner) + execution/verification/reporting agents.</summary>
public static class DefaultAgents
{
    public static IEnumerable<IAgent> All() => PlanningAgents.All().Concat(ExecutionAgents.All());
}
