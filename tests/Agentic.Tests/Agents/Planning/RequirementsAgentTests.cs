using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

namespace Agentic.Tests.Agents.Planning;

public class RequirementsAgentTests
{
    [Fact]
    public async Task Ambiguous_text_yields_exactly_the_expected_ambiguities_in_lexicon_order()
    {
        var doc = await Kit.Requirements(Kit.AmbiguousText);
        Assert.Equal(new[] { "AMB-PERFORMANCE", "AMB-SECURITY", "AMB-REPORTING", "AMB-SCALE", "AMB-QUALITY", "AMB-ACTOR" }, doc.Ambiguities.Select(a => a.Id));
        Assert.Equal(new[] { "high", "high", "high", "medium", "medium", "medium" }, doc.Ambiguities.Select(a => a.Severity));
        Assert.All(doc.Ambiguities, a => Assert.False(a.Resolved));
    }

    [Fact]
    public async Task Ambiguous_text_needs_clarification_and_defers_analytics()
    {
        var doc = await Kit.Requirements(Kit.AmbiguousText);
        Assert.Equal("needs-clarification", doc.Status);
        Assert.Equal(1, doc.Revision);
        Assert.Equal(doc.Ambiguities.Select(a => a.Id), doc.OpenQuestions.Select(q => q.Id));
        // "reporting" with no metric is not committed as analytics until AMB-REPORTING is answered
        Assert.Equal(new[] { "shorten", "redirect" }, doc.Features.Select(f => f.Id));
        Assert.Equal("explicit", doc.Features.Single(f => f.Id == "shorten").Source);
        Assert.Equal("implied", doc.Features.Single(f => f.Id == "redirect").Source);
    }

    [Fact]
    public async Task Evidence_quotes_the_matched_phrase_with_context()
    {
        var doc = await Kit.Requirements(Kit.AmbiguousText);
        var perf = doc.Ambiguities.Single(a => a.Id == "AMB-PERFORMANCE");
        Assert.Equal("fast", perf.Term);
        Assert.Contains("fast", perf.Evidence);
        Assert.Contains("should be", perf.Evidence);
        Assert.Contains("50 ms", perf.AssumedDefault);
        Assert.Contains("latency target", perf.Question);
    }

    [Fact]
    public async Task Answers_for_the_high_ambiguities_clear_the_requirement_and_add_clarified_features()
    {
        var doc = await Kit.Requirements(Kit.AmbiguousText, p: Kit.Answers(Kit.HighAnswers));
        Assert.Equal("clear", doc.Status);
        Assert.Equal(2, doc.Revision);
        foreach (var id in Kit.HighAnswers.Keys)
        {
            var a = doc.Ambiguities.Single(x => x.Id == id);
            Assert.True(a.Resolved);
            Assert.Equal(Kit.HighAnswers[id], a.Resolution);
        }
        var analytics = doc.Features.Single(f => f.Id == "analytics");
        var ratelimit = doc.Features.Single(f => f.Id == "ratelimit");
        Assert.Equal("clarified", analytics.Source);
        Assert.Equal("clarified", ratelimit.Source);
        Assert.Contains("clarified via AMB-REPORTING", analytics.Rationale);
        Assert.Contains("clarified via AMB-SECURITY", ratelimit.Rationale);
        Assert.Equal(Kit.HighAnswers.Count, doc.Answers.Count);
    }

    [Fact]
    public async Task Resolved_answers_leave_medium_ambiguities_as_assumptions_and_retention_appears_with_analytics()
    {
        var doc = await Kit.Requirements(Kit.AmbiguousText, p: Kit.Answers(Kit.HighAnswers));
        Assert.Contains(doc.Assumptions, s => s.StartsWith("AMB-SCALE: single instance"));
        Assert.Contains(doc.Assumptions, s => s.StartsWith("AMB-QUALITY:"));
        Assert.Contains(doc.Assumptions, s => s.StartsWith("AMB-ACTOR:"));
        Assert.Contains(doc.Assumptions, s => s == "AMB-RETENTION: 90 days");
        Assert.Contains(doc.Assumptions, s => s.Contains("in-memory"));
        Assert.DoesNotContain(doc.Assumptions, s => s.StartsWith("AMB-PERFORMANCE"));
        Assert.DoesNotContain(doc.OpenQuestions, q => q.Id == "AMB-PERFORMANCE");
    }

    [Fact]
    public async Task Numeric_answers_override_default_nfrs_as_clarified()
    {
        var answers = new Dictionary<string, string>(Kit.HighAnswers)
        {
            ["AMB-PERFORMANCE"] = "p95 redirect latency under 80 ms at 500 rps",
            ["AMB-RETENTION"] = "keep click data for 30 days",
        };
        var doc = await Kit.Requirements(Kit.AmbiguousText, p: Kit.Answers(answers));
        var latency = doc.Nfrs.Single(n => n.Id == "NFR-PERF-REDIRECT");
        Assert.Equal("80 ms", latency.Target);
        Assert.Equal("clarified", latency.Source);
        var tp = doc.Nfrs.Single(n => n.Id == "NFR-PERF-THROUGHPUT");
        Assert.Equal("500 req/s", tp.Target);
        var ret = doc.Nfrs.Single(n => n.Id == "NFR-PRIVACY-RETENTION");
        Assert.Equal("30 days", ret.Target);
        Assert.Equal("clarified", ret.Source);
        Assert.True(doc.Ambiguities.Single(a => a.Id == "AMB-RETENTION").Resolved);
    }

    [Fact]
    public async Task Default_nfrs_come_from_the_catalog_and_are_assumed()
    {
        var doc = await Kit.Requirements(Kit.ClearText);
        foreach (var id in new[] { "NFR-PERF-REDIRECT", "NFR-PERF-CREATE", "NFR-LIMIT-URL", "NFR-LIMIT-BODY", "NFR-FORMAT-CODE" })
            Assert.Equal("assumed", doc.Nfrs.Single(n => n.Id == id).Source);
        Assert.Equal($"{Kit.Catalog.NfrInt("redirectLatencyP95Ms", 0)} ms", doc.Nfrs.Single(n => n.Id == "NFR-PERF-REDIRECT").Target);
        Assert.Equal("2048 characters", doc.Nfrs.Single(n => n.Id == "NFR-LIMIT-URL").Target);
    }

    [Fact]
    public async Task Clear_text_extracts_explicit_features_and_is_clear()
    {
        var doc = await Kit.Requirements(Kit.ClearText);
        Assert.Equal("clear", doc.Status);
        var ids = doc.Features.Select(f => f.Id).ToList();
        foreach (var id in new[] { "shorten", "redirect", "alias", "expiry", "analytics", "ratelimit", "observability" }) Assert.Contains(id, ids);
        Assert.DoesNotContain("hardening", ids);
        Assert.Equal("explicit", doc.Features.Single(f => f.Id == "alias").Source);
        Assert.Contains("custom alias", doc.Features.Single(f => f.Id == "alias").Rationale);
        Assert.Equal("implied", doc.Features.Single(f => f.Id == "redirect").Source);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        // analytics is selected and no retention given -> only the low-severity retention question remains
        Assert.Equal(new[] { "AMB-RETENTION" }, doc.Ambiguities.Select(a => a.Id));
        Assert.Empty(doc.Ambiguities.Where(a => a.Severity != "low"));
    }

    [Fact]
    public async Task Baseline_workspace_adds_hardening_greenfield_does_not()
    {
        var green = await Kit.Requirements("Add expiry to links");
        var brown = await Kit.Requirements("Add expiry to links", workspace: "baseline");
        Assert.DoesNotContain(green.Features, f => f.Id == "hardening");
        var h = brown.Features.Single(f => f.Id == "hardening");
        Assert.Equal("implied", h.Source);
        Assert.Contains("Extend and harden", brown.ProblemStatement);
        Assert.StartsWith("Build a URL shortener", green.ProblemStatement);
    }

    [Fact]
    public async Task Actors_are_detected_with_api_client_as_the_default()
    {
        Assert.Equal(new[] { "marketing team" }, (await Kit.Requirements(Kit.AmbiguousText)).Actors);
        Assert.Equal(new[] { "API client" }, (await Kit.Requirements(Kit.ClearText)).Actors);
        Assert.Equal(new[] { "customers", "developers", "admins" }, (await Kit.Requirements("Customers and developers and admins create short links")).Actors);
    }

    [Fact]
    public async Task Concrete_numbers_and_controls_suppress_their_ambiguities()
    {
        var doc = await Kit.Requirements("Shorten links, redirects must be fast (p95 under 50 ms), secure with rate limiting and https only, and scalable to 1000 rps");
        Assert.Empty(doc.Ambiguities);
        Assert.Equal("clear", doc.Status);
    }

    [Fact]
    public async Task Quantifier_fires_for_vague_lists_but_not_for_some_followed_by_a_flagged_term()
    {
        var vague = await Kit.Requirements("Shorten links, track clicks, and some other stuff etc.");
        var q = vague.Ambiguities.Single(a => a.Id == "AMB-QUANTIFIER");
        Assert.Equal("low", q.Severity);
        Assert.Contains("some", q.Question);
        Assert.Equal("Only items stated explicitly", q.AssumedDefault);
        var covered = await Kit.Requirements(Kit.AmbiguousText);
        Assert.DoesNotContain(covered.Ambiguities, a => a.Id == "AMB-QUANTIFIER");
    }

    [Fact]
    public async Task Output_is_deterministic()
    {
        var a = await Kit.Run(new RequirementsAgent(), Kit.Ctx(Kit.Scenario(Kit.AmbiguousText), p: Kit.Answers(Kit.HighAnswers)));
        var b = await Kit.Run(new RequirementsAgent(), Kit.Ctx(Kit.Scenario(Kit.AmbiguousText), p: Kit.Answers(Kit.HighAnswers)));
        Assert.Equal(Json.Canonical(a.Artifacts["requirements"]), Json.Canonical(b.Artifacts["requirements"]));
        Assert.Equal(Json.Serialize(a.Decisions), Json.Serialize(b.Decisions));
    }

    [Fact]
    public async Task Malformed_answers_are_ignored_and_revision_param_is_honoured()
    {
        var bad = await Kit.Requirements(Kit.AmbiguousText, p: new() { ["answers"] = "{not json" });
        Assert.Equal("needs-clarification", bad.Status);
        Assert.Equal(1, bad.Revision);
        var rev = await Kit.Requirements(Kit.AmbiguousText, p: new() { ["answers"] = Json.Serialize(Kit.HighAnswers), ["revision"] = "5" });
        Assert.Equal(5, rev.Revision);
    }

    [Fact]
    public async Task Decisions_record_each_assumption_and_each_implied_feature()
    {
        var r = await Kit.Run(new RequirementsAgent(), Kit.Ctx(Kit.Scenario(Kit.AmbiguousText)));
        var titles = r.Decisions.Select(d => d.Title).ToList();
        Assert.Contains("Assume AMB-SCALE", titles);
        Assert.Contains("Assume AMB-QUALITY", titles);
        Assert.Contains("Assume AMB-ACTOR", titles);
        Assert.Contains("Add implied feature 'redirect'", titles);
        Assert.Contains(titles, t => t.StartsWith("Defer feature 'analytics'"));
        Assert.Contains("Request human clarification before design", titles);
        Assert.All(r.Decisions, d => Assert.False(string.IsNullOrWhiteSpace(d.Rationale)));
    }

    [Fact]
    public void Agent_metadata_and_registry()
    {
        var agent = new RequirementsAgent();
        Assert.Equal("requirements", agent.Name);
        Assert.Equal("requirements", RequirementsAgent.AgentName);
        Assert.Empty(agent.Autonomy.WriteGlobs);
        Assert.Equal(new[] { "requirements", "codebase-analyst", "architect", "test-planner" }, PlanningAgents.All().Select(a => a.Name));
        Assert.True(new CodebaseAnalystAgent().Autonomy.CanRunTests);
    }
}
