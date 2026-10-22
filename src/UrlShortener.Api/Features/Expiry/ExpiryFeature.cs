using System.Text.Json;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Expiry;

public sealed class ExpiryFeature : IFeature
{
    public string Id => "expiry";

    public int Order => 50;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IShortenHook, ExpiryShortenHook>();
        services.AddSingleton<IRedirectHook, ExpiryRedirectHook>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>Reads the optional "ttlSeconds" field and sets ExpiresAt on the pending link.</summary>
public sealed class ExpiryShortenHook : IShortenHook
{
    public const long MaxTtlSeconds = 31_536_000;

    public int Order => 50;

    public Task OnShortenAsync(ShortenContext context)
    {
        if (!context.Body.TryGetProperty("ttlSeconds", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return Task.CompletedTask;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var ttl) || ttl < 1 || ttl > MaxTtlSeconds)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_ttl", $"'ttlSeconds' must be an integer between 1 and {MaxTtlSeconds}.");
        }

        context.Pending.ExpiresAt = context.Pending.CreatedAt.AddSeconds(ttl);
        return Task.CompletedTask;
    }
}

/// <summary>Rejects redirects for links whose ExpiresAt has passed (410 link_expired), using the injected clock.</summary>
public sealed class ExpiryRedirectHook : IRedirectHook
{
    private readonly TimeProvider _time;

    public ExpiryRedirectHook(TimeProvider time) => _time = time;

    public int Order => 50;

    public Task BeforeAsync(RedirectContext context)
    {
        if (context.Link.ExpiresAt is { } expiresAt && _time.GetUtcNow() >= expiresAt)
        {
            throw new ApiException(StatusCodes.Status410Gone, "link_expired", "This link has expired.");
        }

        return Task.CompletedTask;
    }
}
