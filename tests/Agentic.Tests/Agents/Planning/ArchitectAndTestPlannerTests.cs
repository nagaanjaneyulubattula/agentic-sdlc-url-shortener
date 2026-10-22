using System.Text.Json.Nodes;
using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Planning;

public class ArchitectAndTestPlannerTests
{
    private static async Task<(AgentResult result, DesignDoc design, Dictionary<string, JsonNode> store)> Design(
        string text, string workspace = "empty", ImpactReport? impact = null, Dictionary<string, string>? answers = null)
    {
        var store = new Dictionary<string, JsonNode>();
        var scenario = Kit.Scenario(text, workspace, workspace == "baseline" ? "brownfield" : "greenfield");
        Kit.Publish(store, await Kit.Run(new RequirementsAgent(), Kit.Ctx(scenario, p: answers is null ? null : Kit.Answers(answers))));
        if (impact is not null) store["impact"] = Json.ToNode(impact);
        var r = await Kit.Run(new ArchitectAgent(), Kit.Ctx(scenario, artifacts: store, nodeId: "design"));
        Kit.Publish(store, r);
        return (r, Kit.Get<DesignDoc>(r, "design"), store);
    }

    private static ImpactReport Impact(IEnumerable<string> installed, params Finding[] findings) =>
        new(installed.ToList(), new(), new(), new(), new(), new(), new(), new(), new(), findings.ToList(), null);

    private static readonly Finding SchemeFinding = new("FIND-URL-SCHEME", "high", "CWE-601", "src/UrlShortener.Api/Core/UrlValidator.cs", 1, "weak", "allow only http/https");
    private static readonly Finding RandomFinding = new("FIND-SEC-004", "high", "CWE-338", "src/UrlShortener.Api/Core/CodeGenerator.cs", 4, "System.Random", "use CSPRNG");

    [Fact]
    public async Task Greenfield_closure_adds_required_features_and_orders_them_by_dependency()
    {
        var (result, design, _) = await Design("Add custom aliases and link expiry");
        Assert.Equal("greenfield", design.Mode);
        Assert.Equal(new[] { "hardening", "shorten", "redirect", "alias", "expiry" }, design.Features);
        Assert.Equal(new[] { "hardening" }, design.AlreadyInstalled);
        Assert.Equal(new[] { "shorten", "redirect", "alias", "expiry" }, design.ToInstall);
        foreach (var f in design.Features) foreach (var req in Kit.Catalog.Get(f).Requires) Assert.True(design.Features.IndexOf(req) < design.Features.IndexOf(f));
        Assert.Contains(result.Decisions, d => d.Title == "Always include 'hardening'");
        Assert.Contains(result.Decisions, d => d.Title == "Slice implementation order");
    }

    [Fact]
    public async Task Closure_decision_is_recorded_for_features_added_only_by_dependency()
    {
        var store = new Dictionary<string, JsonNode>();
        // a requirement doc that only asks for alias: shorten arrives by closure
        var doc = new RequirementsDoc("alias", "p", new() { "API client" }, "clear",
            new() { new FeatureReq("alias", "Custom aliases", "explicit", "r") }, new(), new(), new(), new(), new(), 1);
        store["requirements"] = Json.ToNode(doc);
        var r = await Kit.Run(new ArchitectAgent(), Kit.Ctx(Kit.Scenario("alias"), artifacts: store));
        var design = Kit.Get<DesignDoc>(r, "design");
        Assert.Equal(new[] { "hardening", "shorten", "alias" }, design.Features);
        var d = Assert.Single(r.Decisions, x => x.Title == "Add 'shorten' by dependency closure");
        Assert.Contains("alias requires shorten", d.Rationale);
    }

    [Fact]
    public async Task Expansion_creates_one_implementer_node_per_task_with_gates_deps_and_approvals()
    {
        var (result, design, _) = await Design(Kit.ClearText);
        var exp = result.Expansion!;
        Assert.Equal(new[] { "integrate" }, exp.JoinInto);
        Assert.Equal(design.Tasks.Select(t => t.Id), exp.Nodes.Select(n => n.Id));
        Assert.All(exp.Nodes, n =>
        {
            Assert.Equal("implementation", n.Stage);
            Assert.Equal("implementer", n.Agent);
            Assert.Equal(new[] { "workspace-ready" }, n.EntryGates);
            Assert.Equal(new[] { "files-written", "policy-clean", "syntax-valid" }, n.ExitGates);
            Assert.Equal("design", n.ExpandedBy);
            Assert.Equal(2, n.Retry.MaxRetries);
            Assert.Equal(50, n.Retry.BackoffMs);
            Assert.Equal("implementer-clean-room", n.Fallback!.Agent);
            Assert.StartsWith("design", n.Deps[0]);
            Assert.Equal("scaffold", n.Deps[1]);
            Assert.Equal("slice", n.Params["mode"]);
        });
        var analytics = exp.Nodes.Single(n => n.Id == "implement:analytics");
        Assert.Equal(new[] { "design", "scaffold", "implement:shorten", "implement:redirect" }, analytics.Deps);
        Assert.Equal(new[] { "task:analytics" }, analytics.Reads);
        Assert.Equal("analytics", analytics.Params["feature"]);
        Assert.Equal(("before", "high"), (analytics.Approval!.When, analytics.Approval.Risk));
        Assert.Contains("Change control", analytics.Approval.Reason);
        Assert.Contains("privacy", analytics.Approval.Reason);
        Assert.NotNull(exp.Nodes.Single(n => n.Id == "implement:ratelimit").Approval);
        Assert.Null(exp.Nodes.Single(n => n.Id == "implement:alias").Approval);
        Assert.Null(exp.Nodes.Single(n => n.Id == "implement:expiry").Approval);
        Assert.Equal(new[] { "design", "scaffold" }, exp.Nodes.Single(n => n.Id == "implement:shorten").Deps);
        Assert.DoesNotContain(exp.Nodes, n => n.Id == "implement:hardening");
    }

    [Fact]
    public async Task Task_artifacts_are_published_per_feature()
    {
        var (result, design, store) = await Design(Kit.ClearText);
        foreach (var t in design.Tasks)
        {
            Assert.True(result.Artifacts.ContainsKey("task:" + t.Feature));
            var task = store["task:" + t.Feature].Deserialize<TaskSpec>()!;
            Assert.Equal(t, task with { }, new TaskSpecComparer());
            Assert.Equal("slice", task.Kind);
            Assert.Empty(task.ModifiesExisting);
        }
        var rl = design.Tasks.Single(t => t.Feature == "ratelimit");
        Assert.True(rl.Sensitive);
        Assert.Equal(new[] { "implement:shorten" }, rl.DependsOn);
        Assert.Equal("Security control on the write path (change-control review)", rl.SensitiveReason);
    }

    private sealed class TaskSpecComparer : IEqualityComparer<TaskSpec>
    {
        public bool Equals(TaskSpec? a, TaskSpec? b) => Json.Serialize(a) == Json.Serialize(b);
        public int GetHashCode(TaskSpec t) => Json.Serialize(t).GetHashCode();
    }

    [Fact]
    public async Task Brownfield_with_blocking_findings_schedules_a_patch_node_needing_approval()
    {
        var impact = Impact(new[] { "shorten", "redirect", "manage" }, SchemeFinding, RandomFinding);
        var (result, design, _) = await Design("Add link expiry", "baseline", impact);
        Assert.Equal("brownfield", design.Mode);
        // existing capabilities are preserved in the target set
        Assert.Contains("manage", design.Features);
        Assert.Equal(new[] { "shorten", "redirect", "manage" }, design.AlreadyInstalled);
        Assert.Equal(new[] { "hardening", "expiry" }, design.ToInstall);
        var patch = design.Tasks.Single(t => t.Feature == "hardening");
        Assert.Equal("patch", patch.Kind);
        Assert.Equal(Kit.Catalog.Get("hardening").Modifies, patch.ModifiesExisting);
        Assert.True(patch.Sensitive);
        var node = result.Expansion!.Nodes.Single(n => n.Id == "implement:hardening");
        Assert.Equal("patch", node.Params["mode"]);
        Assert.Equal("hardening", node.Params["feature"]);
        Assert.Equal("high", node.Approval!.Risk);
        Assert.Contains("UrlValidator.cs", node.Approval.Reason);
        Assert.Contains("modifies existing", node.Approval.Reason);
        // expiry depends on shorten/redirect, which are installed: no dependency edge to a non-existent node
        var expiry = result.Expansion.Nodes.Single(n => n.Id == "implement:expiry");
        Assert.Equal(new[] { "design", "scaffold" }, expiry.Deps);
        Assert.Null(expiry.Approval);
    }

    [Fact]
    public async Task Brownfield_without_blocking_findings_treats_hardening_as_already_satisfied()
    {
        var impact = Impact(new[] { "shorten", "redirect" });
        var (result, design, _) = await Design("Add link expiry", "baseline", impact);
        Assert.Contains("hardening", design.AlreadyInstalled);
        Assert.DoesNotContain("hardening", design.ToInstall);
        Assert.DoesNotContain(result.Expansion!.Nodes, n => n.Id == "implement:hardening");
        Assert.Contains("regression (existing baseline suite must stay green)", (await TestPlan(design)).Levels);
    }

    [Fact]
    public async Task OpenApi_describes_routes_request_fields_and_extra_responses()
    {
        var (_, design, _) = await Design(Kit.ClearText);
        var paths = design.OpenApi["paths"]!.AsObject();
        Assert.Equal("3.0.3", design.OpenApi["openapi"]!.GetValue<string>());
        Assert.Equal("URL Shortener API", design.OpenApi["info"]!["title"]!.GetValue<string>());
        Assert.Equal("1.0.0", design.OpenApi["info"]!["version"]!.GetValue<string>());
        Assert.True(paths.ContainsKey("/api/links"));
        Assert.True(paths.ContainsKey("/{code}"));
        Assert.True(paths.ContainsKey("/api/links/{code}/stats"));
        Assert.True(paths.ContainsKey("/healthz"));
        var post = paths["/api/links"]!["post"]!;
        var props = post["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("url"));
        Assert.True(props.ContainsKey("alias"));
        Assert.True(props.ContainsKey("ttlSeconds"));
        Assert.Equal("integer", props["ttlSeconds"]!["type"]!.GetValue<string>());
        Assert.Equal("url", post["requestBody"]!["content"]!["application/json"]!["schema"]!["required"]![0]!.GetValue<string>());
        Assert.NotNull(post["responses"]!["201"]);
        Assert.NotNull(post["responses"]!["429"]);
        Assert.NotNull(post["responses"]!["409"]);
        Assert.NotNull(paths["/{code}"]!["get"]!["responses"]!["410"]);
        Assert.Equal("code", paths["/{code}"]!["get"]!["parameters"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adrs_and_risks_reflect_the_selected_features()
    {
        var (_, expiryOnly, _) = await Design("Add link expiry");
        Assert.Equal(new[] { "ADR-001", "ADR-002", "ADR-003", "ADR-006", "ADR-007" }, expiryOnly.Adrs.Select(a => a.Id));
        var (_, full, _) = await Design(Kit.ClearText);
        Assert.Equal(new[] { "ADR-001", "ADR-002", "ADR-003", "ADR-004", "ADR-005", "ADR-006", "ADR-007" }, full.Adrs.Select(a => a.Id));
        Assert.All(full.Adrs, a => { Assert.Equal("accepted", a.Status); Assert.NotEmpty(a.Alternatives); });

        var covered = full.Risks.Single(r => r.Id == "R-ABUSE");
        Assert.Equal("Low – mitigated and covered by AC-RL-1, AC-RL-2", covered.Residual);
        var (_, withManage, _) = await Design("Shorten links and let admins delete them");
        var accepted = withManage.Risks.Single(r => r.Id == "R-UNAUTH-DELETE");
        Assert.StartsWith("Accepted – ", accepted.Residual);
        Assert.Contains(full.Risks, r => r.Id == "R-OPEN-REDIRECT");
    }

    [Fact]
    public async Task Components_and_data_model_describe_the_selected_slices()
    {
        var (_, design, _) = await Design(Kit.ClearText);
        Assert.Equal("core", design.Components[0].Id);
        var analytics = design.Components.Single(c => c.Id == "analytics");
        Assert.Equal("src/UrlShortener.Api/Features/Analytics", analytics.Path);
        Assert.Contains("afterRedirect", analytics.Responsibility);
        Assert.Contains("ILinkStore", design.DataModel);
        Assert.Contains("Click event", design.DataModel);
        Assert.Contains("Expiry", design.DataModel);
    }

    [Fact]
    public async Task Architect_output_is_deterministic_across_runs()
    {
        var (a, _, _) = await Design(Kit.ClearText);
        var (b, _, _) = await Design(Kit.ClearText);
        Assert.Equal(Json.Canonical(a.Artifacts["design"]), Json.Canonical(b.Artifacts["design"]));
        Assert.Equal(Json.Serialize(a.Expansion), Json.Serialize(b.Expansion));
        Assert.Equal(Json.Serialize(a.Decisions), Json.Serialize(b.Decisions));
        Assert.Equal(a.Artifacts.Keys.OrderBy(k => k), b.Artifacts.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task Clarified_answers_flow_through_to_the_design()
    {
        var (_, design, _) = await Design(Kit.AmbiguousText, answers: Kit.HighAnswers);
        Assert.Contains("analytics", design.Features);
        Assert.Contains("ratelimit", design.Features);
        Assert.Contains(design.Tasks, t => t.Id == "implement:analytics" && t.Sensitive);
    }

    // ── test planner ─────────────────────────────────────────────────────────────────────────────

    private static async Task<TestPlan> TestPlan(DesignDoc design)
    {
        var store = new Dictionary<string, JsonNode> { ["design"] = Json.ToNode(design) };
        var r = await Kit.Run(new TestPlannerAgent(), Kit.Ctx(Kit.Scenario("x"), artifacts: store));
        return Kit.Get<TestPlan>(r, "test-plan");
    }

    [Fact]
    public async Task Test_plan_requires_every_acceptance_criterion_of_the_full_target_set()
    {
        var (_, design, _) = await Design("Add custom aliases and link expiry");
        var plan = await TestPlan(design);
        Assert.Equal(design.Features, plan.AcceptanceByFeature.Keys);
        var expected = design.Features.SelectMany(f => Kit.Catalog.Get(f).Acceptance.Select(a => a.Id)).ToList();
        Assert.Equal(expected, plan.RequiredAcceptanceIds);
        foreach (var id in new[] { "AC-HARD-1", "AC-SHORT-1", "AC-REDIR-3", "AC-ALIAS-2", "AC-EXP-2" }) Assert.Contains(id, plan.RequiredAcceptanceIds);
        Assert.DoesNotContain("AC-ANA-1", plan.RequiredAcceptanceIds);
        Assert.Equal(plan.RequiredAcceptanceIds.Count, plan.RequiredAcceptanceIds.Distinct().Count());
        Assert.Equal("AC-ALIAS-1", plan.AcceptanceByFeature["alias"][0].Id);
    }

    [Fact]
    public async Task Test_plan_levels_and_gates_depend_on_the_mode()
    {
        var (_, green, _) = await Design(Kit.ClearText);
        var greenPlan = await TestPlan(green);
        Assert.Equal(new[] { "unit", "integration (WebApplicationFactory, in-process HTTP)", "policy/security scan", "traceability (every acceptance criterion has a test)" }, greenPlan.Levels);
        Assert.Equal(new[] { "tests-green", "traceability-complete", "security-passed", "release-go" }, greenPlan.Gates);

        var (_, brown, _) = await Design("Add link expiry", "baseline", Impact(new[] { "shorten", "redirect" }, SchemeFinding));
        var brownPlan = await TestPlan(brown);
        Assert.Equal(5, brownPlan.Levels.Count);
        Assert.Equal("regression (existing baseline suite must stay green)", brownPlan.Levels[^1]);
    }

    [Fact]
    public void Agent_names_and_autonomy_are_stable()
    {
        Assert.Equal("architect", ArchitectAgent.AgentName);
        Assert.Equal("test-planner", TestPlannerAgent.AgentName);
        Assert.Equal("codebase-analyst", CodebaseAnalystAgent.AgentName);
        Assert.All(PlanningAgents.All(), a => Assert.Empty(a.Autonomy.WriteGlobs));
    }
}
