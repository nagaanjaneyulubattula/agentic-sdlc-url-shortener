namespace Agentic.Orchestrator.Agents;

/// <summary>Registry of the execution / verification / reporting agents.</summary>
public static class ExecutionAgents
{
    public static IEnumerable<IAgent> All() => new IAgent[]
    {
        new ImplementerAgent(),
        new ImplementerCleanRoomAgent(),
        new TesterAgent(),
        new SecurityReviewerAgent(),
        new DocumenterAgent(),
        new ReleaseManagerAgent(),
        new SummarizerAgent(),
    };
}
