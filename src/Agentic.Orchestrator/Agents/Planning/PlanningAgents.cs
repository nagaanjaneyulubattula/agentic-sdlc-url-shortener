namespace Agentic.Orchestrator.Agents;

public static class PlanningAgents
{
    public static IEnumerable<IAgent> All() => new IAgent[] { new RequirementsAgent(), new CodebaseAnalystAgent(), new ArchitectAgent(), new TestPlannerAgent() };
}
