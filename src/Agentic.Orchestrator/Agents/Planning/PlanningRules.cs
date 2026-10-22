namespace Agentic.Orchestrator.Agents;

/// <summary>Small deterministic rules shared by the planning agents (analyst, architect) so they never disagree.</summary>
internal static class PlanningRules
{
    /// <summary>Semantic findings (raised by the analyst on UrlValidator) that the hardening patch addresses.</summary>
    private static readonly string[] UrlHardeningFindingIds = { "FIND-URL-SCHEME", "FIND-URL-HOSTS" };

    public static bool IsBlocking(Finding f) => f.Severity is "high" or "critical";

    /// <summary>True when the finding lies in the patch's concerns (it touches a file the patch rewrites, or is a URL-validation finding for hardening).</summary>
    public static bool Addresses(FeatureDef patch, Finding f) =>
        patch.Modifies.Contains(f.File, StringComparer.Ordinal) ||
        (patch.Id == "hardening" && UrlHardeningFindingIds.Contains(f.Id, StringComparer.Ordinal));

    /// <summary>A patch is only needed when the scan found a high/critical finding it addresses; otherwise the codebase already satisfies it.</summary>
    public static bool PatchNeeded(FeatureDef patch, IEnumerable<Finding>? findings) =>
        findings is not null && findings.Any(f => IsBlocking(f) && Addresses(patch, f));

    /// <summary>User-visible API behaviour changes caused by installing a feature (beyond new routes / request fields).</summary>
    public static IEnumerable<string> BehaviourChanges(string featureId) => featureId switch
    {
        "expiry" => new[] { "GET /{code} may now return 410 (link_expired) for expired links" },
        "ratelimit" => new[] { "POST /api/links may now return 429 with a Retry-After header" },
        "alias" => new[] { "POST /api/links may now return 409 (alias_taken) and 400 for reserved or invalid aliases" },
        "idempotency" => new[] { "POST /api/links may now replay a stored 201 (Idempotent-Replay: true) or return 422 (idempotency_key_reuse)" },
        "analytics" => new[] { "GET /{code} now records a click (side effect only; redirect behaviour is unchanged)" },
        "hardening" => new[] { "POST /api/links now rejects non-http(s) and internal URLs with 400" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Extra HTTP responses a feature adds to a catalog route (catalog 'responses' only describe the core behaviour).</summary>
    public static readonly (string Feature, string Route, string Status, string Text)[] ExtraResponses =
    {
        ("expiry", "GET /:code", "410", "Link expired (link_expired)"),
        ("ratelimit", "POST /api/links", "429", "Too many requests (Retry-After header)"),
        ("alias", "POST /api/links", "409", "Alias already taken (alias_taken)"),
        ("idempotency", "POST /api/links", "422", "Idempotency-Key reused with a different body (idempotency_key_reuse)"),
    };
}
