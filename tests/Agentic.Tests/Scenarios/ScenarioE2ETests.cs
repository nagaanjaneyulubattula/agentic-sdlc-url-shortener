using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Scenarios;

[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollection;

/// <summary>
/// End-to-end proof: the real agents generate / modify a real workspace and the real <c>dotnet test</c> verifies it.
/// Scripted approver plays the human (clearly labelled "scripted:&lt;scenario&gt;" in the audit trail).
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public class ScenarioE2ETests
{
    private static async Task<(Engine Engine, RunResult Result)> RunScenario(string id, Action<ScenarioSpec>? tweak = null)
    {
        var catalog = FeatureCatalog.Load();
        var scenario = ScenarioLoader.Clone(ScenarioLoader.Load(catalog.RepoRoot, id));
        tweak?.Invoke(scenario);
        var engine = new Engine(scenario, catalog, DefaultAgents.All(), new EngineOptions
        {
            RunsRoot = Path.Combine(Path.GetTempPath(), "agentic-e2e", Guid.NewGuid().ToString("N")),
            TestRunner = new DotnetTestRunner(),
            Approvals = new ScriptedApprovalProvider(scenario),
            Delay = (_, _) => Task.CompletedTask,
        });
        var result = await engine.RunAsync();
        return (engine, result);
    }

    private static T Artifact<T>(Engine e, string name) where T : class =>
        e.State.Artifacts.Latest(name)?.Deserialize<T>() ?? throw new InvalidOperationException($"artifact '{name}' missing");

    private static void AssertGovernanceInvariants(Engine e, RunResult r)
    {
        Assert.True(e.Audit.Verify().Valid, "audit hash chain must be intact");
        Assert.True(File.Exists(Path.Combine(r.RunDirectory, "audit.jsonl")));
        Assert.True(File.Exists(Path.Combine(r.RunDirectory, "state.json")));
        Assert.True(File.Exists(Path.Combine(r.RunDirectory, "metrics.json")));
        Assert.NotEmpty(e.State.Decisions);
    }

    [Fact]
    public async Task Greenfield_builds_verifies_and_documents_the_service_with_retries_and_rollback()
    {
        var (e, r) = await RunScenario("greenfield");

        Assert.True(r.Status == RunStatus.Completed, $"status {r.Status}: {r.StopReason}\n{string.Join("\n", e.State.Nodes.Values.Where(n => n.Error is not null).Select(n => n.Spec.Id + ": " + n.Error))}");
        Assert.Equal("GO", r.ReleaseDecision);
        AssertGovernanceInvariants(e, r);

        // decomposition → dynamic expansion into parallel implementation slices
        var implement = e.State.Nodes.Values.Where(n => n.Spec.Id.StartsWith("implement:")).ToList();
        Assert.Equal(9, implement.Count);
        Assert.All(implement, n => Assert.Equal(NodeStatus.Done, n.Status));
        Assert.Equal(NodeStatus.Skipped, e.State.Nodes["analyze-codebase"].Status);
        var layers = e.BuildGraph().Layers();
        Assert.Contains(layers, l => l.Count(x => x.StartsWith("implement:")) >= 2);   // independent slices share a layer (parallel)

        // injected faults handled by the resilience controls
        Assert.Equal(1, e.State.Nodes["implement:analytics"].Retries);
        Assert.True(e.State.Nodes["implement:expiry"].Rollbacks >= 1);
        var m = e.Metrics();
        Assert.True(m.Retries >= 2 && m.Rollbacks >= 1 && m.MttrMs is not null);

        // governance: sensitive slices were gated by a human checkpoint before being written
        Assert.Contains(e.State.Approvals, a => a.NodeId == "implement:analytics" && a.When == "before" && a.Decision == "approved");
        Assert.Contains(e.State.Approvals, a => a.NodeId == "release-readiness" && a.Decision == "approved");

        // validation evidence
        var tests = Artifact<TestReport>(e, "test-report");
        Assert.True(tests.Green && tests.Total >= 40, $"tests: {tests.Passed}/{tests.Total}\n{tests.OutputTail}");
        Assert.Equal(1.0, Artifact<Traceability>(e, "traceability").Coverage);
        Assert.True(Artifact<SecurityReport>(e, "security-report").Passed);
        Assert.Equal("GO", Artifact<ReleaseReadiness>(e, "release-readiness").Decision);

        // outputs
        foreach (var f in new[] { "README.md", "docs/API.md", "docs/ARCHITECTURE.md", "docs/RISKS.md", "src/UrlShortener.Api/Features/FeatureRegistry.cs" })
            Assert.True(File.Exists(Path.Combine(r.WorkspaceRoot, f)), f + " missing");
        var summary = File.ReadAllText(Path.Combine(r.RunDirectory, "ENGINEERING_SUMMARY.md"));
        foreach (var h in new[] { "## 1. Outcome", "## 2. Plan and rationale", "## 3. Artifacts", "## 4. Risks, trade-offs and validation", "## 5. Assumptions", "## 6. Orchestration evidence", "## 7. Decision lineage", "## 8. Limitations" })
            Assert.Contains(h, summary);
    }

    [Fact]
    public async Task Brownfield_reproduces_defects_assesses_impact_and_applies_a_change_controlled_fix()
    {
        var (e, r) = await RunScenario("brownfield");

        Assert.True(r.Status == RunStatus.Completed, $"status {r.Status}: {r.StopReason}\n{string.Join("\n", e.State.Nodes.Values.Where(n => n.Error is not null).Select(n => n.Spec.Id + ": " + n.Error))}");
        Assert.Equal("GO", r.ReleaseDecision);
        AssertGovernanceInvariants(e, r);

        // codebase reasoning
        var scan = Artifact<ImpactReport>(e, "codebase-scan");
        Assert.Contains(scan.InstalledFeatures, f => f == "manage");
        Assert.Contains(scan.Findings, f => f.Id == "FIND-URL-SCHEME");
        Assert.Contains(scan.Findings, f => f.Cwe == "CWE-338");
        Assert.True(scan.BaselineTests?.Green == true, "baseline suite is green before the change (regression safety net)");
        var impact = Artifact<ImpactReport>(e, "impact");
        Assert.Contains(impact.Impacted, i => i.Path.EndsWith("UrlValidator.cs") && i.Risk == "high");
        Assert.Contains(impact.ModifiedFiles, p => p.EndsWith("CodeGenerator.cs"));
        Assert.True(Artifact<ReproReport>(e, "repro").Reproduced, "defect reproduced by a failing test BEFORE the fix");
        Assert.Equal(NodeStatus.Skipped, e.State.Nodes["scaffold"].Status);

        // change control: modifying security-sensitive existing files required a human and the first (broken) patch was rolled back
        Assert.Contains(e.State.Approvals, a => a.NodeId == "implement:hardening" && a.When == "before");
        Assert.True(e.State.Nodes["implement:hardening"].Rollbacks >= 1);
        Assert.Contains(e.State.Changes, c => c.Path.EndsWith("UrlValidator.cs") && c.Op == "modify");

        // after the fix everything is green, including the regression tests that failed before
        var tests = Artifact<TestReport>(e, "test-report");
        Assert.True(tests.Green, tests.OutputTail);
        Assert.Equal(1.0, Artifact<Traceability>(e, "traceability").Coverage);
        var rel = Artifact<ReleaseReadiness>(e, "release-readiness");
        Assert.Contains(rel.Checklist, c => c.Id == "REL-06" && c.Status == "pass");
        Assert.True(File.Exists(Path.Combine(r.WorkspaceRoot, "CHANGELOG.md")));
    }

    [Fact]
    public async Task Ambiguous_requirement_is_clarified_by_a_human_and_a_midrun_change_triggers_a_minimal_replan()
    {
        var (e, r) = await RunScenario("ambiguous");

        Assert.True(r.Status == RunStatus.Completed, $"status {r.Status}: {r.StopReason}\n{string.Join("\n", e.State.Nodes.Values.Where(n => n.Error is not null).Select(n => n.Spec.Id + ": " + n.Error))}");
        AssertGovernanceInvariants(e, r);

        // the agent refused to guess: asked the human about the high-severity ambiguities
        var clar = e.State.Approvals.Single(a => a.Kind == "clarification");
        Assert.Equal("requirements", clar.NodeId);
        Assert.Contains(e.Audit.Entries, x => x.Type == "clarification.applied");
        var req = Artifact<RequirementsDoc>(e, "requirements");
        Assert.Contains(req.Features, f => f.Id == "analytics");
        Assert.Contains(req.Ambiguities, a => a.Id == "AMB-PERFORMANCE" && a.Resolved);
        Assert.NotEmpty(req.Assumptions);   // medium/low ambiguities became explicit assumptions

        // change request: requirement artifact changed upstream → only affected nodes were re-planned
        Assert.Contains(req.Features, f => f.Id == "expiry" && f.Source == "amended");
        Assert.DoesNotContain(req.Features, f => f.Id == "ratelimit");
        Assert.True(e.State.Replans.Count >= 1);
        Assert.Equal(NodeStatus.Cancelled, e.State.Nodes["implement:ratelimit"].Status);
        Assert.Equal(NodeStatus.Done, e.State.Nodes["implement:expiry"].Status);
        Assert.Equal(1, e.State.Nodes["implement:redirect"].Generation);   // untouched work was not redone
        Assert.True(e.State.Nodes["design"].Generation >= 2);
        Assert.False(File.Exists(Path.Combine(r.WorkspaceRoot, "src/UrlShortener.Api/Features/RateLimit/RateLimitFeature.cs")));
        Assert.True(File.Exists(Path.Combine(r.WorkspaceRoot, "src/UrlShortener.Api/Features/Expiry/ExpiryFeature.cs")));
        Assert.True(Artifact<TestReport>(e, "test-report").Green);
    }

    [Fact]
    public async Task Critical_policy_violation_safe_stops_and_the_run_can_be_resumed_after_the_cause_is_removed()
    {
        var (e, r) = await RunScenario("chaos-safe-stop");

        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Contains("SEC-001", r.StopReason);
        Assert.Equal(NodeStatus.Failed, e.State.Nodes["implement:alias"].Status);
        Assert.Equal(1, e.State.Nodes["implement:alias"].Attempts);   // critical → no retries
        Assert.Contains(e.State.Violations, v => v.Rule == "SEC-001" && v.Severity == "critical");
        Assert.False(File.Exists(Path.Combine(r.WorkspaceRoot, "src/UrlShortener.Api/Core/__InjectedLeak.cs")), "leaking file rolled back");
        Assert.Equal(NodeStatus.Blocked, e.State.Nodes["integrate"].Status);
        Assert.True(e.Audit.Verify().Valid);
        var doneBefore = e.State.Nodes.Values.Where(n => n.Status == NodeStatus.Done).ToDictionary(n => n.Spec.Id, n => n.Generation);

        e.State.Scenario.Faults.Clear();                   // operator removed the cause
        var r2 = await e.ResumeAsync();

        Assert.True(r2.Status == RunStatus.Completed, $"{r2.Status}: {r2.StopReason}");
        Assert.All(doneBefore, kv => Assert.Equal(kv.Value, e.State.Nodes[kv.Key].Generation));   // completed work preserved, not redone
        Assert.True(Artifact<TestReport>(e, "test-report").Green);
        Assert.True(e.Audit.Verify().Valid);
    }
}
