using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Redirect;

public sealed class RedirectFeature : IFeature
{
    public string Id => "redirect";

    public int Order => 20;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/{code}", RedirectEndpoint.HandleAsync);
}

public static class RedirectEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string code)
    {
        var services = context.RequestServices;
        var link = services.GetRequiredService<ILinkStore>().Get(code);
        if (link is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Unknown short code.");
        }

        var hooks = services.GetServices<IRedirectHook>().OrderBy(h => h.Order).ToArray();
        var redirectContext = new RedirectContext(context, link);

        foreach (var hook in hooks)
        {
            await hook.BeforeAsync(redirectContext);
        }

        services.GetRequiredService<IMetrics>().Increment("redirects_total");

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("UrlShortener.Redirect");
        await HookRunner.RunAfterRedirectAsync(hooks, redirectContext, logger);

        return Results.Redirect(ToLocation(link.Url));
    }

    // Header values must be ASCII; percent-encode (via Uri) only when the stored URL contains other characters.
    private static string ToLocation(string url) =>
        url.All(c => c < 128) ? url : new Uri(url).AbsoluteUri;
}
