using System.Text.Json;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Shorten;

public sealed record ShortenResponse(string Code, string ShortUrl, string Url, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

public static class ShortenEndpoint
{
    /// <summary>Bounded retries when a generated code collides with an existing one.</summary>
    public const int MaxCodeAttempts = 8;

    public static async Task<IResult> HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ShortenerOptions>();
        var store = services.GetRequiredService<ILinkStore>();
        var metrics = services.GetRequiredService<IMetrics>();
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("UrlShortener.Shorten");
        var hooks = services.GetServices<IShortenHook>().OrderBy(h => h.Order).ToArray();

        var raw = await RequestBody.ReadAsync(context, options.MaxBodyBytes);
        using var document = RequestBody.ParseObject(raw);
        var body = document.RootElement;

        if (!body.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_url", "'url' is required and must be a string.");
        }

        var url = (urlElement.GetString() ?? string.Empty).Trim();
        if (url.Length > options.MaxUrlLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "url_too_long", $"'url' must not exceed {options.MaxUrlLength} characters.");
        }

        var (ok, errorCode) = UrlValidator.Validate(url);
        if (!ok)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, errorCode ?? "invalid_url", "The url is not allowed.");
        }

        var pending = new PendingLink { Url = url, CreatedAt = time.GetUtcNow() };
        var shortenContext = new ShortenContext(context, body, pending);

        string? customCode = null;
        foreach (var hook in hooks)
        {
            customCode = await hook.ResolveCodeAsync(shortenContext);
            if (customCode is not null)
            {
                break;
            }
        }

        foreach (var hook in hooks)
        {
            await hook.OnShortenAsync(shortenContext);
        }

        var link = customCode is not null
            ? CreateCustom(store, pending, customCode, shortenContext.ConflictErrorCode)
            : CreateGenerated(store, services.GetRequiredService<CodeGenerator>(), options, pending);

        metrics.Increment("links_created_total");

        var response = new ShortenResponse(link.Code, ShortUrl.Build(context, options, link.Code), link.Url, link.CreatedAt, link.ExpiresAt);
        await HookRunner.RunAfterShortenAsync(hooks, shortenContext, link, StatusCodes.Status201Created, response, logger);
        return ApiResults.Json(StatusCodes.Status201Created, response);
    }

    private static Link CreateCustom(ILinkStore store, PendingLink pending, string code, string conflictCode)
    {
        pending.Code = code;
        var link = new Link(code, pending.Url, pending.CreatedAt, pending.ExpiresAt);
        if (!store.TryCreate(link))
        {
            throw new ApiException(StatusCodes.Status409Conflict, conflictCode, "That code is already in use.");
        }

        return link;
    }

    private static Link CreateGenerated(ILinkStore store, CodeGenerator generator, ShortenerOptions options, PendingLink pending)
    {
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = generator.Generate(options.CodeLength);
            if (ReservedCodes.IsReserved(code))
            {
                continue;
            }

            var link = new Link(code, pending.Url, pending.CreatedAt, pending.ExpiresAt);
            if (store.TryCreate(link))
            {
                pending.Code = code;
                return link;
            }
        }

        throw new ApiException(StatusCodes.Status503ServiceUnavailable, "code_generation_failed", "Could not allocate a unique code. Please retry.");
    }
}
