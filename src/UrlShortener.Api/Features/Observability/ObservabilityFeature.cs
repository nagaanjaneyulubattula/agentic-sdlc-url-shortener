using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Observability;

public sealed class ObservabilityFeature : IFeature
{
    public string Id => "observability";

    public int Order => 70;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => ApiResults.Json(StatusCodes.Status200OK, new { status = "ok" }));

        endpoints.MapGet("/readyz", (HttpContext context) =>
        {
            try
            {
                context.RequestServices.GetRequiredService<ILinkStore>().Ping();
                return ApiResults.Json(StatusCodes.Status200OK, new { status = "ready" });
            }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("UrlShortener.Observability").LogWarning(ex, "Readiness check failed");
                return ApiResults.Json(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
            }
        });

        endpoints.MapGet("/metrics", (HttpContext context) =>
            ApiResults.Json(StatusCodes.Status200OK, context.RequestServices.GetRequiredService<IMetrics>().Snapshot()));
    }
}
