using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Execution;

public sealed class ExecutionAgentsTests
{
    [Fact]
    public void All_ReturnsTheSevenAgentsWithUniqueNames()
    {
        var names = ExecutionAgents.All().Select(a => a.Name).ToList();
        Assert.Equal(7, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(
            new[] { "implementer", "implementer-clean-room", "tester", "security-reviewer", "documenter", "release-manager", "summarizer" }.OrderBy(x => x),
            names.OrderBy(x => x));
    }

    [Fact]
    public void AgentNameConstants_MatchInstances()
    {
        Assert.Equal(ImplementerAgent.AgentName, new ImplementerAgent().Name);
        Assert.Equal(ImplementerCleanRoomAgent.AgentName, new ImplementerCleanRoomAgent().Name);
        Assert.Equal(TesterAgent.AgentName, new TesterAgent().Name);
        Assert.Equal(SecurityReviewerAgent.AgentName, new SecurityReviewerAgent().Name);
        Assert.Equal(DocumenterAgent.AgentName, new DocumenterAgent().Name);
        Assert.Equal(ReleaseManagerAgent.AgentName, new ReleaseManagerAgent().Name);
        Assert.Equal(SummarizerAgent.AgentName, new SummarizerAgent().Name);
    }

    [Fact]
    public void WriteBoundaries_ComeFromTheAgentDefinitions_AndOnlyBuildersWrite()
    {
        var byName = ExecutionAgents.All().ToDictionary(a => a.Name);
        Assert.Equal(new[] { "src/**", "tests/**", ".gitignore" }, byName["implementer"].Autonomy.WriteGlobs);
        Assert.Equal(new[] { "src/**", "tests/**", ".gitignore" }, byName["implementer-clean-room"].Autonomy.WriteGlobs);
        Assert.Equal(new[] { "README.md", "docs/**", "CHANGELOG.md" }, byName["documenter"].Autonomy.WriteGlobs);
        foreach (var readOnly in new[] { "tester", "security-reviewer", "release-manager", "summarizer" })
            Assert.Empty(byName[readOnly].Autonomy.WriteGlobs);
        Assert.True(byName["tester"].Autonomy.CanRunTests);
        Assert.Single(byName.Values.Where(a => a.Autonomy.CanRunTests));
    }
}
