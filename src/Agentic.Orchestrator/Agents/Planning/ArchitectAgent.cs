using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Designs the target system from the requirements (and, for brownfield, the impact report): feature closure, components,
/// data model, OpenAPI contract, ADRs, risk register and one implementation task per feature still to install.
/// Returns an <see cref="Expansion"/> so the engine can grow the graph with one <c>implement:&lt;feature&gt;</c> node per task.
/// </summary>
public sealed class ArchitectAgent : IAgent
{
    public const string AgentName = "architect";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy { get; } = new(Array.Empty<string>());

    public Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        var cat = ctx.Catalog;
        var result = new AgentResult();
        var req = ctx.Read<RequirementsDoc>("requirements");
        var impact = ctx.Has("impact") ? ctx.ReadOrDefault<ImpactReport>("impact") : null;
        var brownfield = impact is not null || string.Equals(ctx.Scenario.Workspace, "baseline", StringComparison.OrdinalIgnoreCase);

        var requested = req.Features.Select(f => f.Id).Where(cat.Features.ContainsKey).Distinct().ToList();
        var installed = impact?.InstalledFeatures.Where(cat.Features.ContainsKey).Distinct().ToList()
                        ?? (brownfield ? cat.Baseline.Features.Where(cat.Features.ContainsKey).ToList() : new List<string>());

        // Target = what was asked for + hardening + (brownfield) everything already installed, so existing capabilities are never dropped.
        var target = new List<string>(requested);
        if (cat.Features.ContainsKey("hardening")) target.Add("hardening");
        if (brownfield) target.AddRange(installed);
        var features = cat.Closure(target);

        bool PatchSatisfied(FeatureDef patch) =>
            !brownfield || (impact is not null && !PlanningRules.PatchNeeded(patch, impact.Findings));
        var alreadyInstalled = features.Where(f => cat.Get(f).IsPatch ? PatchSatisfied(cat.Get(f)) : installed.Contains(f)).ToList();
        var toInstall = features.Except(alreadyInstalled).ToList();

        // ── components / data model / OpenAPI ──────────────────────────────────────────────────
        var components = new List<ComponentInfo>
        {
            new("core", string.Join(", ", cat.Core.Dirs.Where(d => d.StartsWith("src/", StringComparison.Ordinal))),
                "Host, hook interfaces (IRequestGate, IShortenHook, IRedirectHook), URL validation, code generation, link store, error handling, security headers"),
        };
        foreach (var id in features)
        {
            var def = cat.Get(id);
            if (def.IsPatch || def.Slice is null) continue;
            var uses = def.Hooks?.Uses is { Count: > 0 } u ? $" (uses hooks: {string.Join(", ", u)})" : "";
            components.Add(new ComponentInfo(id, cat.Core.SliceSrcDir.Replace("{Slice}", def.Slice), def.Title + uses));
        }

        var openApi = BuildOpenApi(cat, features);
        var dataModel = BuildDataModel(features);

        // ── ADRs ────────────────────────────────────────────────────────────────────────────────
        var has = features.ToHashSet(StringComparer.Ordinal);
        var adrs = new List<Adr>
        {
            new("ADR-001", "Redirect with 302 (not 301)", "accepted",
                "Short links can expire, be deleted and be counted; browsers cache 301 responses indefinitely.",
                "Use 302 Found with Cache-Control: no-store.",
                new() { "301 Moved Permanently", "307/308 temporary redirects" },
                "Every click reaches the service, so analytics, expiry and deletion stay accurate; cost is one extra request per click and no browser/CDN offload."),
            new("ADR-002", "In-memory link store behind ILinkStore", "accepted",
                "The prototype needs persistence semantics (atomic create, get, delete) without operating a database.",
                "Implement ILinkStore with a ConcurrentDictionary; all features depend only on the interface.",
                new() { "SQLite/EF Core", "Redis", "SQL Server" },
                "Zero-ops and deterministic tests; the trade-off is no durability and a single instance. Swap in Redis/SQL behind the same interface to productionise."),
            new("ADR-003", "Vertical feature slices with hook interfaces", "accepted",
                "Features must be added or removed without editing each other, and be implementable in parallel by separate agents.",
                "Each feature is a self-contained slice that plugs into core via IRequestGate / IShortenHook / IRedirectHook and a generated FeatureRegistry.",
                new() { "Layered controllers/services with feature flags", "Microservices per feature" },
                "Open/closed extension and a small blast radius for changes; the price is an indirection layer and a convention to learn."),
        };
        if (has.Contains("analytics") || has.Contains("ratelimit"))
            adrs.Add(new("ADR-004", "HMAC-hashed client key instead of raw IP", "accepted",
                "Rate limiting and unique-visitor counting need a client identifier, but raw IPs are personal data.",
                "Core/ClientKey.cs is the only code that reads the remote address and returns a keyed HMAC-SHA256 hash used everywhere else.",
                new() { "Store raw IP addresses", "Truncate IPs (/24)", "No client identification" },
                "Privacy by design and policy-enforceable (COMP-001/002); hashes cannot be reversed to look up a person, and salt rotation resets unique counts."));
        if (has.Contains("ratelimit"))
            adrs.Add(new("ADR-005", "In-memory token bucket for rate limiting", "accepted",
                "Link creation must be protected from automated abuse without external infrastructure.",
                "Per-client token bucket on POST /api/links, LRU-bounded map, enforced by an IRequestGate.",
                new() { "Fixed window counter", "Distributed limiter (Redis)", "API gateway only" },
                "Simple and burst-friendly; limits are per instance, so a distributed limiter is needed to scale out."));
        if (has.Contains("expiry") || has.Contains("ratelimit") || has.Contains("idempotency"))
            adrs.Add(new("ADR-006", "Injectable TimeProvider", "accepted",
                "Expiry, token refill and key retention depend on time, which makes tests flaky if the wall clock is used.",
                "All time comes from an injected TimeProvider; tests use a controllable fake.",
                new() { "DateTime.UtcNow everywhere", "Thread.Sleep in tests" },
                "Deterministic, fast tests and no clock-skew surprises; requires discipline to never call the system clock directly."));
        adrs.Add(new("ADR-007", "Hardening: allowlist URL validation and CSPRNG codes", "accepted",
            "A shortener is an attractive open-redirect/SSRF vector, and predictable codes allow enumeration of other users' links.",
            "Accept only http/https, reject credentials and loopback/private/link-local/metadata hosts, and generate codes from RandomNumberGenerator.",
            new() { "Denylist of known-bad schemes", "Resolve DNS and check addresses", "Math.Random-based codes" },
            "Strong default safety; DNS names that resolve to private addresses are not detected without resolution (accepted residual)."));
        foreach (var a in adrs)
            result.Decide($"{a.Id}: {a.Title}", $"{a.Context} {a.Decision}", a.Alternatives, a.Consequences);

        // ── risks ───────────────────────────────────────────────────────────────────────────────
        var risks = new List<RiskItem>();
        foreach (var id in features)
            foreach (var r in cat.Get(id).Risks)
                risks.Add(new RiskItem(r.Id, r.Scenario, r.Likelihood, r.Impact, r.Mitigation, r.Validation.ToList(),
                    r.Validation.Count > 0 ? $"Low – mitigated and covered by {string.Join(", ", r.Validation)}" : $"Accepted – {r.Mitigation}"));

        // ── tasks ───────────────────────────────────────────────────────────────────────────────
        var tasks = new List<TaskSpec>();
        foreach (var id in toInstall)
        {
            var def = cat.Get(id);
            var modifies = def.IsPatch ? def.Modifies.ToList() : new List<string>();
            var sensitivePaths = modifies.Where(ChangeControl.IsSensitivePath).ToList();
            var sensitive = def.Sensitive || sensitivePaths.Count > 0;
            var reason = def.SensitiveReason ?? (sensitive
                ? (sensitivePaths.Count > 0 ? "Modifies security-sensitive code (" + string.Join(", ", sensitivePaths.Select(p => System.IO.Path.GetFileName(p))) + ")" : "Security-sensitive change")
                : null);
            tasks.Add(new TaskSpec("implement:" + id, id, def.IsPatch ? "patch" : "slice",
                def.Requires.Where(toInstall.Contains).Select(r => "implement:" + r).ToList(), modifies, sensitive, reason));
        }
        foreach (var t in tasks) result.Add("task:" + t.Feature, t);

        var design = new DesignDoc(brownfield ? "brownfield" : "greenfield", features, toInstall, alreadyInstalled, components, dataModel, openApi, adrs, risks, tasks);
        result.Add("design", design);

        // ── expansion ───────────────────────────────────────────────────────────────────────────
        var nodes = new List<NodeSpec>();
        foreach (var t in tasks)
        {
            var needsApproval = t.Sensitive || t.ModifiesExisting.Count > 0;
            var why = new List<string>();
            if (t.ModifiesExisting.Count > 0) why.Add("modifies existing file(s): " + string.Join(", ", t.ModifiesExisting));
            if (t.Sensitive) why.Add("sensitive: " + (t.SensitiveReason ?? "security-sensitive change"));
            nodes.Add(new NodeSpec
            {
                Id = t.Id,
                Stage = "implementation",
                Agent = "implementer",
                Deps = new List<string> { "design", "scaffold" }.Concat(t.DependsOn).ToList(),
                Reads = new() { "task:" + t.Feature },
                EntryGates = new() { "workspace-ready" },
                ExitGates = new() { "files-written", "policy-clean", "syntax-valid" },
                Params = new() { ["mode"] = t.Kind == "patch" ? "patch" : "slice", ["feature"] = t.Feature },
                Retry = new RetryPolicy(2, 50),
                Fallback = new FallbackSpec("implementer-clean-room"),
                Approval = needsApproval ? new ApprovalSpec("before", "high", $"Change control: {t.Id} {string.Join("; ", why)}") : null,
                ExpandedBy = "design",
            });
        }
        result.Expansion = new Expansion(nodes, new() { "integrate" });

        // ── decisions ───────────────────────────────────────────────────────────────────────────
        foreach (var id in features.Where(f => !requested.Contains(f) && f != "hardening" && !installed.Contains(f)))
        {
            var dependents = features.Where(f => cat.Get(f).Requires.Contains(id)).ToList();
            result.Decide($"Add '{id}' by dependency closure", $"{string.Join(", ", dependents)} requires {id}.",
                new[] { $"Reject the design because '{id}' was not requested" }, "Slightly more scope in exchange for a consistent, runnable feature set.");
        }
        if (features.Contains("hardening"))
            result.Decide("Always include 'hardening'",
                alreadyInstalled.Contains("hardening")
                    ? (brownfield ? "The scan found no high/critical URL-validation or code-generation findings, so the hardening patch is already satisfied." : "The core ships hardened URL validation and CSPRNG codes, so hardening is already satisfied.")
                    : "The scan found high/critical findings in URL validation or code generation; the hardening patch is scheduled with human approval.",
                new[] { "Skip hardening unless asked" }, "Security-by-default over minimal scope.");
        result.Decide("Slice implementation order",
            $"Implement in dependency order (requirements before dependents, then catalog order): {string.Join(" → ", toInstall)}.",
            new[] { "Alphabetical order", "Fully parallel with no ordering" },
            "Deterministic and dependency-safe; independent slices still run in parallel because nodes only depend on their real requirements.");

        return Task.FromResult(result);
    }

    // ── data model ──────────────────────────────────────────────────────────────────────────────
    private static string BuildDataModel(List<string> features)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Link");
        sb.AppendLine("`record Link(string Code, string Url, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt)` – codes are case-sensitive and unique.");
        sb.AppendLine();
        sb.AppendLine("## ILinkStore contract");
        sb.AppendLine("- `bool TryCreate(Link link)` – atomic insert; returns false (no change) when the code exists.");
        sb.AppendLine("- `Link? Get(string code)`");
        sb.AppendLine("- `bool Delete(string code)` – false when the code is unknown.");
        sb.AppendLine("- `void Ping()` – readiness probe; throws when the store is unreachable.");
        if (features.Contains("analytics"))
        {
            sb.AppendLine();
            sb.AppendLine("## Click event");
            sb.AppendLine("`ClickEvent(Code, OccurredAt, Referrer?, ClientKey)` – `ClientKey` is an HMAC-SHA256 hash (never a raw IP). " +
                          "Events are kept in a per-link ring buffer (10,000) while counters keep totals accurate.");
        }
        if (features.Contains("expiry"))
        {
            sb.AppendLine();
            sb.AppendLine("## Expiry");
            sb.AppendLine("`ExpiresAt = CreatedAt + ttlSeconds` (1..31,536,000); a link is expired when `TimeProvider.GetUtcNow() >= ExpiresAt`.");
        }
        return sb.ToString().TrimEnd();
    }

    // ── OpenAPI ─────────────────────────────────────────────────────────────────────────────────
    private static JsonObject BuildOpenApi(FeatureCatalog cat, List<string> features)
    {
        const string createRoute = "POST /api/links";
        var ops = new SortedDictionary<string, SortedDictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var id in features)
        {
            foreach (var r in cat.Get(id).Routes)
            {
                var path = Regex.Replace(r.Path, @":(\w+)", "{$1}");
                var routeKey = $"{r.Method.ToUpperInvariant()} {r.Path}";
                var op = new JsonObject
                {
                    ["operationId"] = $"{id}_{r.Method.ToLowerInvariant()}_{Regex.Replace(path.Trim('/'), @"[^A-Za-z0-9]+", "_").Trim('_')}",
                    ["summary"] = r.Summary,
                    ["tags"] = new JsonArray(id),
                };
                var parameters = new JsonArray();
                foreach (Match pm in Regex.Matches(path, @"\{(\w+)\}"))
                    parameters.Add(new JsonObject { ["name"] = pm.Groups[1].Value, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } });

                var bodyProps = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (r.Request is not null) foreach (var (k, v) in r.Request) bodyProps[k] = v;
                if (routeKey == createRoute)
                    foreach (var f in features)
                        foreach (var rf in cat.Get(f).RequestFields.Where(x => x.Route == createRoute))
                        {
                            if (rf.Field.Contains("(header)", StringComparison.OrdinalIgnoreCase))
                                parameters.Add(new JsonObject
                                {
                                    ["name"] = rf.Field.Replace("(header)", "").Trim(), ["in"] = "header", ["required"] = false,
                                    ["description"] = rf.Type, ["schema"] = new JsonObject { ["type"] = "string" },
                                });
                            else bodyProps[rf.Field] = rf.Type;
                        }
                if (parameters.Count > 0) op["parameters"] = parameters;
                if (bodyProps.Count > 0)
                {
                    var props = new JsonObject();
                    var required = new JsonArray();
                    foreach (var (name, typeText) in bodyProps)
                    {
                        props[name] = new JsonObject { ["type"] = SchemaType(typeText), ["description"] = typeText };
                        if (typeText.Contains("(required)", StringComparison.OrdinalIgnoreCase)) required.Add(name);
                    }
                    var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
                    if (required.Count > 0) schema["required"] = required;
                    op["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } },
                    };
                }

                var responses = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var (code, text) in r.Responses ?? new Dictionary<string, string> { ["200"] = "OK" }) responses[code] = text;
                foreach (var x in PlanningRules.ExtraResponses.Where(x => x.Route == routeKey && features.Contains(x.Feature))) responses[x.Status] = x.Text;
                var resp = new JsonObject();
                foreach (var (code, text) in responses) resp[code] = new JsonObject { ["description"] = text };
                op["responses"] = resp;

                if (!ops.TryGetValue(path, out var byMethod)) ops[path] = byMethod = new(StringComparer.Ordinal);
                byMethod[r.Method.ToLowerInvariant()] = op;
            }
        }

        var paths = new JsonObject();
        foreach (var (path, byMethod) in ops)
        {
            var item = new JsonObject();
            foreach (var (method, op) in byMethod) item[method] = op;
            paths[path] = item;
        }
        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "URL Shortener API", ["version"] = "1.0.0" },
            ["paths"] = paths,
        };
    }

    private static string SchemaType(string typeText) =>
        typeText.StartsWith("integer", StringComparison.OrdinalIgnoreCase) ? "integer" :
        typeText.StartsWith("boolean", StringComparison.OrdinalIgnoreCase) ? "boolean" : "string";
}
