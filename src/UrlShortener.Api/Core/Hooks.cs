using System.Text.Json;

namespace UrlShortener.Api.Core;

/// <summary>
/// Runs before routing/endpoints. A non-null result short-circuits the request (429, idempotent replay...).
/// Gates must scope themselves (check method and path) because they see every request.
/// </summary>
public interface IRequestGate
{
    int Order { get; }

    Task<IResult?> BeforeAsync(HttpContext context);
}

/// <summary>The link being created. Hooks may mutate it before it is stored.</summary>
public sealed class PendingLink
{
    public required string Url { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Code { get; set; }
}

public sealed class ShortenContext
{
    public ShortenContext(HttpContext http, JsonElement body, PendingLink pending)
    {
        Http = http;
        Body = body;
        Pending = pending;
    }

    public HttpContext Http { get; }

    /// <summary>The parsed JSON request body (always a JSON object).</summary>
    public JsonElement Body { get; }

    public PendingLink Pending { get; }

    /// <summary>Error code used for the 409 when a hook-provided code already exists.</summary>
    public string ConflictErrorCode { get; set; } = "code_taken";
}

public interface IShortenHook
{
    int Order { get; }

    /// <summary>Return a custom code (e.g. an alias) or null to let core generate one. May throw <see cref="ApiException"/>.</summary>
    Task<string?> ResolveCodeAsync(ShortenContext context) => Task.FromResult<string?>(null);

    /// <summary>Validate the request / mutate the pending link. May throw <see cref="ApiException"/>.</summary>
    Task OnShortenAsync(ShortenContext context) => Task.CompletedTask;

    /// <summary>Runs after the link is stored. Failures are logged and swallowed.</summary>
    Task AfterShortenAsync(ShortenContext context, Link created, int status, object responseBody) => Task.CompletedTask;
}

public sealed class RedirectContext
{
    public RedirectContext(HttpContext http, Link link)
    {
        Http = http;
        Link = link;
    }

    public HttpContext Http { get; }

    public Link Link { get; }
}

public interface IRedirectHook
{
    int Order { get; }

    /// <summary>May throw <see cref="ApiException"/> to stop the redirect (e.g. 410 Gone).</summary>
    Task BeforeAsync(RedirectContext context) => Task.CompletedTask;

    /// <summary>Side effects only. Core invokes this inside try/catch: failures are logged and swallowed.</summary>
    Task AfterAsync(RedirectContext context) => Task.CompletedTask;
}

/// <summary>Invokes "after" hooks in isolation so a failing side effect can never break the main response.</summary>
public static class HookRunner
{
    public static async Task RunAfterRedirectAsync(IEnumerable<IRedirectHook> hooks, RedirectContext context, ILogger logger)
    {
        foreach (var hook in hooks.OrderBy(h => h.Order))
        {
            try
            {
                await hook.AfterAsync(context);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "afterRedirect hook {Hook} failed; redirect is unaffected", hook.GetType().Name);
            }
        }
    }

    public static async Task RunAfterShortenAsync(IEnumerable<IShortenHook> hooks, ShortenContext context, Link created, int status, object body, ILogger logger)
    {
        foreach (var hook in hooks.OrderBy(h => h.Order))
        {
            try
            {
                await hook.AfterShortenAsync(context, created, status, body);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "afterShorten hook {Hook} failed; response is unaffected", hook.GetType().Name);
            }
        }
    }
}
