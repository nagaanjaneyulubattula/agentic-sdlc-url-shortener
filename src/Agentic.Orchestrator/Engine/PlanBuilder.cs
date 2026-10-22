namespace Agentic.Orchestrator;

/// <summary>
/// Builds the initial SDLC dependency graph. Implementation nodes are NOT part of it: the architect expands the plan
/// dynamically (one node per feature) and the engine joins them into <c>integrate</c> – so the graph is non-linear and re-plannable.
/// Parallel paths: analysis ∥ requirements; test-plan ∥ scaffold ∥ implement:*; security-scan ∥ verify-full ∥ verify-traceability ∥ document.
/// </summary>
public static class PlanBuilder
{
    private static readonly string[] AllReadsForRelease =
        { "requirements", "design", "integration", "test-report", "traceability", "security-report", "docs", "impact", "repro" };

    public static List<NodeSpec> Initial(ScenarioSpec scenario) => new()
    {
        new NodeSpec
        {
            Id = "requirements", Stage = "requirements", Agent = "requirements",
            ExitGates = { "requirements-valid", "requirements-clear" },
            Approval = new ApprovalSpec("after", "medium", "Requirements baseline sign-off (problem statement, features, assumptions)"),
            Retry = new RetryPolicy(1, 50), Rollback = false,
        },
        new NodeSpec
        {
            Id = "analyze-codebase", Stage = "analysis", Agent = "codebase-analyst", Params = { ["mode"] = "scan" },
            Condition = "workspace:baseline", Rollback = false, Retry = new RetryPolicy(1, 100),
        },
        new NodeSpec
        {
            Id = "reproduce", Stage = "analysis", Agent = "tester", Params = { ["mode"] = "repro" },
            Deps = { "analyze-codebase" }, Condition = "workspace:baseline", Rollback = false, Retry = new RetryPolicy(1, 100),
        },
        new NodeSpec
        {
            Id = "impact-assessment", Stage = "analysis", Agent = "codebase-analyst", Params = { ["mode"] = "impact" },
            Deps = { "requirements", "analyze-codebase" }, Reads = { "requirements", "codebase-scan" },
            Condition = "workspace:baseline", Rollback = false,
        },
        new NodeSpec
        {
            Id = "design", Stage = "design", Agent = "architect",
            Deps = { "requirements", "impact-assessment", "reproduce" }, Reads = { "requirements", "impact", "repro" },
            ExitGates = { "design-valid" },
            Approval = new ApprovalSpec("after", "high", "Architecture and change-plan sign-off before any code is generated"),
            Rollback = false,
        },
        new NodeSpec
        {
            Id = "scaffold", Stage = "implementation", Agent = "implementer", Params = { ["mode"] = "scaffold" },
            Deps = { "design" }, Condition = "workspace:empty", ExitGates = { "policy-clean", "syntax-valid" },
        },
        new NodeSpec
        {
            Id = "test-plan", Stage = "testing", Agent = "test-planner", Deps = { "design" }, Reads = { "design" }, Rollback = false,
        },
        new NodeSpec
        {
            Id = "integrate", Stage = "integration", Agent = "implementer", Params = { ["mode"] = "integrate" },
            Deps = { "design", "scaffold" }, Reads = { "design" }, ExitGates = { "policy-clean", "syntax-valid" },
        },
        new NodeSpec
        {
            Id = "verify-full", Stage = "verification", Agent = "tester", Params = { ["mode"] = "full" },
            Deps = { "integrate" }, Reads = { "integration" }, ExitGates = { "tests-green" },
            Retry = new RetryPolicy(1, 200), Rollback = false,
        },
        new NodeSpec
        {
            Id = "verify-traceability", Stage = "verification", Agent = "tester", Params = { ["mode"] = "traceability" },
            Deps = { "integrate", "test-plan" }, Reads = { "test-plan", "integration" }, ExitGates = { "traceability-complete" },
            Retry = new RetryPolicy(0, 0), Rollback = false,
        },
        new NodeSpec
        {
            Id = "security-scan", Stage = "verification", Agent = "security-reviewer",
            Deps = { "integrate" }, Reads = { "integration" }, ExitGates = { "security-passed" },
            Retry = new RetryPolicy(0, 0), Rollback = false,
        },
        new NodeSpec
        {
            Id = "document", Stage = "documentation", Agent = "documenter",
            Deps = { "integrate", "test-plan" }, Reads = { "requirements", "design", "integration", "impact", "test-plan" },
            ExitGates = { "policy-clean" },
        },
        new NodeSpec
        {
            Id = "release-readiness", Stage = "release", Agent = "release-manager",
            Deps = { "verify-full", "verify-traceability", "security-scan", "document" },
            Reads = AllReadsForRelease.ToList(),
            Approval = new ApprovalSpec("after", "high", "Release go/no-go sign-off (human owns the final quality decision)"),
            Rollback = false,
        },
        new NodeSpec
        {
            Id = "summarize", Stage = "release", Agent = "summarizer", Deps = { "release-readiness" },
            Reads = AllReadsForRelease.Concat(new[] { "test-plan", "release-readiness" }).ToList(), Rollback = false,
        },
    };
}

/// <summary>Seeds the workspace before the run: empty (greenfield) or the v1 baseline codebase with seeded defects (brownfield).</summary>
public static class WorkspaceSeeder
{
    public static int Seed(FeatureCatalog catalog, ScenarioSpec scenario, string root)
    {
        Directory.CreateDirectory(root);
        if (scenario.Workspace != "baseline") return 0;
        var copied = 0;
        void Copy(string rel)
        {
            var dest = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(catalog.ResolveSource(rel, baseline: true), dest, overwrite: true);
            copied++;
        }
        foreach (var rel in catalog.CoreFiles(baseline: true)) Copy(rel);
        foreach (var f in catalog.Baseline.Features) foreach (var rel in catalog.FeatureFiles(f)) Copy(rel);
        var registry = Path.Combine(root, catalog.Core.RegistryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, catalog.RenderRegistry(catalog.Baseline.Features));
        File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\nobj/\n");
        return copied + 2;
    }
}
