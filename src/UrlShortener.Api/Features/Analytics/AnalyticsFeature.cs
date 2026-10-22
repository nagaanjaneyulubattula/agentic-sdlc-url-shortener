using Microsoft.Extensions.Options;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Analytics;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Per-link cap on retained click events (ring buffer). Totals are kept in counters and stay exact.</summary>
    public int MaxEventsPerLink { get; set; } = 10_000;

    public int RetentionDays { get; set; } = 90;
}

public sealed class AnalyticsFeature : IFeature
{
    public const int DefaultDays = 7;

    public string Id => "analytics";

    public int Order => 60;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnalyticsOptions>(configuration.GetSection(AnalyticsOptions.SectionName));
        services.AddSingleton<IClickStore, InMemoryClickStore>();
        services.AddSingleton<IRedirectHook, AnalyticsRedirectHook>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Routes.Links + "/{code}/stats", (HttpContext context, string code) => HandleStats(context, code));

    private static IResult HandleStats(HttpContext context, string code)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<IOptions<AnalyticsOptions>>().Value;

        var days = DefaultDays;
        if (context.Request.Query.TryGetValue("days", out var raw) &&
            (!int.TryParse(raw.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out days) ||
             days < 1 || days > options.RetentionDays))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_days", $"'days' must be an integer between 1 and {options.RetentionDays}.");
        }

        if (services.GetRequiredService<ILinkStore>().Get(code) is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Unknown short code.");
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var snapshot = services.GetRequiredService<IClickStore>().Snapshot(code);
        var from = now.AddDays(-days);
        var window = snapshot.Events.Where(e => e.At >= from).ToList();

        return ApiResults.Json(StatusCodes.Status200OK, new
        {
            code,
            totalClicks = snapshot.TotalClicks,
            uniqueVisitors = window.Select(e => e.ClientKey).Distinct(StringComparer.Ordinal).Count(),
            clicksByDay = window
                .GroupBy(e => e.At.UtcDateTime.Date)
                .OrderBy(g => g.Key)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), clicks = g.Count() })
                .ToList(),
            topReferrers = window
                .Where(e => e.ReferrerHost is not null)
                .GroupBy(e => e.ReferrerHost!, StringComparer.Ordinal)
                .Select(g => new { host = g.Key, clicks = g.Count() })
                .OrderByDescending(r => r.clicks)
                .ThenBy(r => r.host, StringComparer.Ordinal)
                .Take(10)
                .ToList(),
        });
    }
}
