using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Turns free-text requirements into a structured <see cref="RequirementsDoc"/>: actors, catalog features, NFRs and – crucially –
/// a fixed lexicon of ambiguity detectors with stable ids (scenarios, the console and tests refer to them).
/// Deterministic, rule-based and offline: same input -> same output. Human answers arrive as JSON in <c>Params["answers"]</c>.
/// </summary>
public sealed class RequirementsAgent : IAgent
{
    public const string AgentName = "requirements";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy { get; } = new(Array.Empty<string>());

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed record Rule(
        string Id, string Severity, Regex Trigger, Regex? Suppress,
        Func<Match, string> Question, Func<FeatureCatalog, string> Default);

    // The lexicon is part of the contract: ids, triggers, questions and defaults must not drift.
    private static readonly Rule[] Lexicon =
    {
        new("AMB-PERFORMANCE", "high",
            new(@"\b(fast|quick(ly)?|performant|performance|low[- ]latency|real[- ]time|snappy)\b", Ci),
            new(@"\b\d+(?:\.\d+)?\s*(?:milliseconds?|ms|seconds?|secs?|rps|req(?:uests?)?(?:/s)?|s)\b", Ci),
            _ => "What latency target (e.g. p95 redirect latency in ms) and what peak throughput must it meet?",
            c => $"p95 redirect latency ≤ {c.NfrInt("redirectLatencyP95Ms", 50)} ms"),
        new("AMB-SECURITY", "high",
            new(@"\b(secure|security|safe|safely|protect(ed|ion)?|trustworthy)\b", Ci),
            new(@"\b(?:rate[- ]?limit\w*|scheme\w*|ssrf|https?|auth\w*|token\w*|block\w*)\b", Ci),
            _ => "Which threats must be mitigated (abuse/spam, malicious target URLs, enumeration, data privacy) and is authentication required?",
            _ => "Block dangerous schemes/internal hosts, rate-limit creation per client; no authentication in v1"),
        new("AMB-REPORTING", "high",
            new(@"\b(reporting|reports?|insights?|dashboard)\b", Ci),
            new(@"\b(?:clicks?|referr\w*|per day|daily|unique|counts?)\b", Ci),
            _ => "Which metrics, at what granularity, in what format (API/CSV/UI) should reporting provide?",
            _ => "JSON API with total & unique clicks, clicks per day and top referrers"),
        new("AMB-SCALE", "medium",
            new(@"\b(scal(e|able|es|ing)|high(ly)? available|resilien(t|ce)|reliab(le|ility)|robust)\b", Ci),
            new(@"\d", Ci),
            _ => "Expected volume (links, redirects/sec) and availability target?",
            _ => "single instance, ~100 redirects/sec, 99.9% availability"),
        new("AMB-QUALITY", "medium",
            new(@"\b(better|improv(e|ed|ement)|enhanc(e|ed|ement)|modern|nicer|great|good)\b", Ci),
            null,
            _ => "What measurable outcome defines 'better'?",
            _ => "Better = the explicitly listed capabilities only; no UX changes"),
        new("AMB-ACTOR", "medium",
            new(@"\b(team|users?|customers?|everyone|people)\b", Ci),
            new(@"\b(?:auth\w*|log[- ]?in|token\w*|tenan\w*|account\w*|api[- ]key)\b", Ci),
            _ => "Who may create/delete links; is multi-tenancy or authentication needed?",
            _ => "Anonymous API clients; no tenancy; deployment sits behind an authenticated gateway"),
    };

    private static readonly Regex RetentionInfo = new(@"\b(\d+)\s*(day|month|year)s?\b", Ci);
    private static readonly Regex Quantifier = new(@"\b(some|various|several|etc\.?|and so on|stuff|things|a few)(?=\W|$)", Ci);
    private static readonly Regex MsRx = new(@"(\d+)\s*ms\b", Ci);
    private static readonly Regex RpsRx = new(@"(\d+)\s*(?:rps|req/s|requests per second)", Ci);
    private static readonly Regex DayRx = new(@"(\d+)\s*days?\b", Ci);

    public Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        var cat = ctx.Catalog;
        var text = ctx.Scenario.RequirementText ?? "";
        var baseline = string.Equals(ctx.Scenario.Workspace, "baseline", StringComparison.OrdinalIgnoreCase);
        var answers = ParseAnswers(ctx);
        var result = new AgentResult();

        // ── (c) ambiguity detection on the raw text ──────────────────────────────────────────────
        var detected = new List<(Rule rule, Match m)>();
        foreach (var rule in Lexicon)
        {
            var m = rule.Trigger.Match(text);
            if (!m.Success) continue;
            if (rule.Suppress is not null && rule.Suppress.IsMatch(text)) continue;
            detected.Add((rule, m));
        }
        var reportingGate = detected.Any(d => d.rule.Id == "AMB-REPORTING");

        // ── (b) feature extraction ───────────────────────────────────────────────────────────────
        var features = new Dictionary<string, FeatureReq>(StringComparer.Ordinal);
        foreach (var def in CatalogOrder(cat))
        {
            if (def.IsPatch && !baseline) continue; // the greenfield core already ships hardened code
            var hit = FirstKeywordHit(def, text);
            if (hit is null) continue;
            if (def.Id == "analytics" && reportingGate)
            {
                // "reporting" without any named metric is too vague to commit a feature: ask first (AMB-REPORTING).
                result.Decide("Defer feature 'analytics' until AMB-REPORTING is answered",
                    $"'{hit}' only hints at reporting; no metric, granularity or format is named, so analytics is not committed from keywords alone.",
                    new[] { "Add analytics now with the assumed default", "Ignore the reporting wish" },
                    "One extra human question before design vs. risk of building the wrong reporting surface.");
                continue;
            }
            features[def.Id] = new FeatureReq(def.Id, def.Title, "explicit", $"matched '{hit}' in the requirement text");
        }

        void Imply(string id, string why)
        {
            if (features.ContainsKey(id) || !cat.Features.TryGetValue(id, out var def)) return;
            features[id] = new FeatureReq(id, def.Title, "implied", why);
            result.Decide($"Add implied feature '{id}'", why, new[] { $"Leave '{id}' out" }, "Slightly larger scope in exchange for a working end-to-end product.");
        }
        Imply("shorten", "Every URL shortener must be able to create short links.");
        Imply("redirect", "Short links are useless unless they can be resolved; redirect is implied by any shortener.");
        if (baseline) Imply("hardening", "Existing (baseline) code is scanned for known vulnerability classes; the hardening patch is applied only where findings exist.");

        // ── (d) answers: resolve ambiguities, add clarified features, parse numbers ──────────────
        string? Answer(string id) => answers.TryGetValue(id, out var a) ? a : null;
        foreach (var (ambId, answer) in answers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Lexicon.Any(r => string.Equals(r.Id, ambId, StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(ambId, "AMB-RETENTION", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ambId, "AMB-QUANTIFIER", StringComparison.OrdinalIgnoreCase)) continue;
            var canonicalId = ambId.ToUpperInvariant();
            foreach (var def in CatalogOrder(cat))
            {
                if (features.ContainsKey(def.Id) || (def.IsPatch && !baseline)) continue;
                var hit = FirstKeywordHit(def, answer);
                if (hit is null) continue;
                features[def.Id] = new FeatureReq(def.Id, def.Title, "clarified", $"clarified via {canonicalId}: answer mentions '{hit}'");
                result.Decide($"Add clarified feature '{def.Id}'", $"clarified via {canonicalId}: the human answer mentions '{hit}'.",
                    new[] { "Ignore the answer's feature wish" }, "Scope grows only by what the human explicitly confirmed.");
            }
        }
        if (reportingGate && Answer("AMB-REPORTING") is not null && !features.ContainsKey("analytics") && cat.Features.TryGetValue("analytics", out var ana))
        {
            features["analytics"] = new FeatureReq("analytics", ana.Title, "clarified", "clarified via AMB-REPORTING: reporting need confirmed by the human");
            result.Decide("Add clarified feature 'analytics'", "clarified via AMB-REPORTING: the reporting question was answered, so the deferred analytics feature is committed.",
                new[] { "Keep analytics out" }, "Scope grows only by what the human explicitly confirmed.");
        }

        // AMB-RETENTION only exists once analytics is part of the target set.
        // Detection looks at the raw text only, so an answer that supplies the retention period shows up as a *resolved* ambiguity.
        if (features.ContainsKey("analytics") && !RetentionInfo.IsMatch(text))
        {
            detected.Add((new Rule("AMB-RETENTION", "low", new Regex("^$"), null,
                _ => "How long may click data be retained (privacy)?",
                c => $"{c.NfrInt("analyticsRetentionDays", 90)} days"), Match.Empty));
        }

        // AMB-QUANTIFIER: skip "some X" when X already triggers its own, more specific question.
        var otherSpans = detected.Where(d => d.m != Match.Empty).Select(d => (d.m.Index, d.m.Length)).ToList();
        foreach (Match q in Quantifier.Matches(text))
        {
            var qEnd = q.Index + q.Length;
            var covered = otherSpans.Any(s => s.Index >= qEnd && s.Index - qEnd <= 3 && string.IsNullOrWhiteSpace(text.Substring(qEnd, s.Index - qEnd)));
            if (covered) continue;
            detected.Add((new Rule("AMB-QUANTIFIER", "low", Quantifier, null,
                mm => $"Which exact items does '{mm.Value}' stand for?", _ => "Only items stated explicitly"), q));
            break;
        }

        var ambiguities = detected.Select(d =>
        {
            var (rule, m) = d;
            var resolution = Answer(rule.Id);
            var resolved = resolution is not null;
            var term = m == Match.Empty ? "click data retention" : m.Value;
            var evidence = m == Match.Empty ? "analytics is part of the scope but no retention period is stated" : EvidenceOf(text, m);
            return new Ambiguity(rule.Id, term, evidence, rule.Severity, rule.Question(m), rule.Default(cat), resolved, resolution);
        }).ToList();

        // ── (e) NFRs ─────────────────────────────────────────────────────────────────────────────
        var redirectMs = cat.NfrInt("redirectLatencyP95Ms", 50);
        var redirectSrc = "assumed";
        if (Answer("AMB-PERFORMANCE") is { } pa && MsRx.Match(pa) is { Success: true } mm1)
        { redirectMs = int.Parse(mm1.Groups[1].Value); redirectSrc = "clarified"; }
        int? rps = null;
        foreach (var key in new[] { "AMB-PERFORMANCE", "AMB-SCALE" })
            if (rps is null && Answer(key) is { } a && RpsRx.Match(a) is { Success: true } rm) rps = int.Parse(rm.Groups[1].Value);
        var retentionDays = cat.NfrInt("analyticsRetentionDays", 90);
        var retentionSrc = "assumed";
        if (Answer("AMB-RETENTION") is { } ra && DayRx.Match(ra) is { Success: true } dm)
        { retentionDays = int.Parse(dm.Groups[1].Value); retentionSrc = "clarified"; }

        var nfrs = new List<Nfr>
        {
            new("NFR-PERF-REDIRECT", "performance", $"p95 redirect latency ≤ {redirectMs} ms", $"{redirectMs} ms", redirectSrc),
            new("NFR-PERF-CREATE", "performance", $"p95 link creation latency ≤ {cat.NfrInt("createLatencyP95Ms", 200)} ms", $"{cat.NfrInt("createLatencyP95Ms", 200)} ms", "assumed"),
            new("NFR-LIMIT-URL", "limits", $"Destination URLs are at most {cat.NfrInt("maxUrlLength", 2048)} characters", $"{cat.NfrInt("maxUrlLength", 2048)} characters", "assumed"),
            new("NFR-LIMIT-BODY", "limits", $"Request bodies are at most {cat.NfrInt("maxBodyBytes", 16384)} bytes", $"{cat.NfrInt("maxBodyBytes", 16384)} bytes", "assumed"),
            new("NFR-FORMAT-CODE", "format", $"Short codes are {cat.NfrInt("codeLength", 7)}-character base62 strings", $"{cat.NfrInt("codeLength", 7)} characters", "assumed"),
        };
        if (rps is not null) nfrs.Add(new("NFR-PERF-THROUGHPUT", "performance", $"Sustain {rps} requests per second", $"{rps} req/s", "clarified"));
        if (features.ContainsKey("analytics"))
            nfrs.Add(new("NFR-PRIVACY-RETENTION", "privacy", $"Click data is retained for at most {retentionDays} days", $"{retentionDays} days", retentionSrc));

        // ── (f) assumptions, open questions, status, revision ────────────────────────────────────
        var assumptions = new List<string>();
        foreach (var a in ambiguities.Where(a => !a.Resolved && a.Severity != "high"))
        {
            assumptions.Add($"{a.Id}: {a.AssumedDefault}");
            result.Decide($"Assume {a.Id}", $"'{a.Term}' is vague ({a.Severity} severity); proceeding with the default: {a.AssumedDefault}.",
                new[] { "Block until a human answers", "Pick a different default" }, "Speed over certainty; the assumption is recorded and can be revised via a re-run with answers.");
        }
        assumptions.Add("Persistence is in-memory for the prototype (data is lost on restart); the store sits behind ILinkStore");
        assumptions.Add("Single service instance (no distributed state)");
        assumptions.Add("No authentication in v1; deployment sits behind an authenticated gateway");

        var open = ambiguities.Where(a => !a.Resolved).Select(a => new OpenQuestion(a.Id, a.Question)).ToList();
        var needsClarification = ambiguities.Any(a => !a.Resolved && a.Severity == "high");
        if (needsClarification)
            result.Decide("Request human clarification before design",
                $"{ambiguities.Count(a => !a.Resolved && a.Severity == "high")} high-severity ambiguity(ies) would change the architecture: " +
                string.Join(", ", ambiguities.Where(a => !a.Resolved && a.Severity == "high").Select(a => a.Id)) + ".",
                new[] { "Proceed on assumed defaults", "Reject the requirement" }, "Costs a human round-trip but avoids rework on a wrong design.");

        var revision = ctx.Params.TryGetValue("revision", out var rev) && int.TryParse(rev, out var r) ? r : (answers.Count > 0 ? 2 : 1);

        var actors = DetectActors(text);
        var ordered = features.Values.OrderBy(f => cat.Features[f.Id].Order).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var doc = new RequirementsDoc(
            text, BuildProblem(baseline, ordered, actors, needsClarification, cat), actors,
            needsClarification ? "needs-clarification" : "clear",
            ordered, nfrs, ambiguities, assumptions, open, answers.ToDictionary(k => k.Key, v => v.Value), revision);
        return Task.FromResult(result.Add("requirements", doc));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<FeatureDef> CatalogOrder(FeatureCatalog cat) =>
        cat.Features.Values.OrderBy(f => f.Order).ThenBy(f => f.Id, StringComparer.Ordinal);

    private static string? FirstKeywordHit(FeatureDef def, string text)
    {
        string? best = null; var bestIdx = int.MaxValue;
        foreach (var kw in def.Keywords)
        {
            Regex rx;
            try { rx = new Regex(kw, Ci); } catch (ArgumentException) { rx = new Regex(Regex.Escape(kw), Ci); }
            var m = rx.Match(text);
            if (m.Success && m.Index < bestIdx) { best = m.Value; bestIdx = m.Index; }
        }
        return best;
    }

    private static string EvidenceOf(string text, Match m)
    {
        var start = Math.Max(0, m.Index - 20);
        var end = Math.Min(text.Length, m.Index + m.Length + 20);
        var snippet = Regex.Replace(text[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }

    private static Dictionary<string, string> ParseAnswers(AgentContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ctx.Params.TryGetValue("answers", out var json) || string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var s = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                if (!string.IsNullOrWhiteSpace(s)) map[p.Name] = s!.Trim();
            }
        }
        catch (JsonException) { /* malformed answers are treated as "no answers" */ }
        return map;
    }

    private static List<string> DetectActors(string text)
    {
        var actors = new List<string>();
        void Check(string pattern, string actor) { if (Regex.IsMatch(text, pattern, Ci)) actors.Add(actor); }
        Check(@"\bmarketing\s+team\b", "marketing team");
        Check(@"\bcustomers?\b", "customers");
        Check(@"\b(developers?|engineers?)\b", "developers");
        Check(@"\badmin(istrator)?s?\b", "admins");
        Check(@"\b(end[- ]?users?|users?)\b", "end users");
        if (actors.Count == 0) actors.Add("API client");
        return actors;
    }

    private static string BuildProblem(bool baseline, List<FeatureReq> features, List<string> actors, bool vague, FeatureCatalog cat)
    {
        var caps = features.Where(f => f.Id is not ("shorten" or "redirect" or "hardening"))
            .Select(f => cat.Features[f.Id].Title).Select(t => char.ToLowerInvariant(t[0]) + t[1..]).ToList();
        var list = caps.Count switch { 0 => "", 1 => caps[0], _ => string.Join(", ", caps.Take(caps.Count - 1)) + " and " + caps[^1] };
        var first = baseline
            ? "Extend and harden the existing URL shortener service" + (list.Length > 0 ? $" with {list}" : "") + "."
            : "Build a URL shortener service that creates and redirects short links" + (list.Length > 0 ? $", with {list}" : "") + ".";
        var second = $"Primary users: {string.Join(", ", actors)}{(vague ? "; vague goals are pending clarification" : "")}.";
        return first + " " + second;
    }
}
