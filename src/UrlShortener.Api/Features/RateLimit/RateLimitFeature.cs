using Microsoft.Extensions.Options;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.RateLimit;

/// <summary>Configuration section "RateLimit".</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public double Capacity { get; set; } = 10;

    public double RefillPerSecond { get; set; } = 1;

    /// <summary>Upper bound on tracked client keys (least recently used keys are dropped).</summary>
    public int MaxKeys { get; set; } = 10_000;
}

public sealed class RateLimitFeature : IFeature
{
    public string Id => "ratelimit";

    public int Order => 5;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.AddSingleton<TokenBucketLimiter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RateLimitOptions>>().Value;
            return new TokenBucketLimiter(options.Capacity, options.RefillPerSecond, options.MaxKeys);
        });
        services.AddSingleton<IRequestGate, RateLimitGate>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>Per-client token bucket on POST /api/links. Redirects and every other route are never limited.</summary>
public sealed class RateLimitGate : IRequestGate
{
    private readonly TokenBucketLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ShortenerOptions _options;
    private readonly IMetrics _metrics;

    public RateLimitGate(TokenBucketLimiter limiter, TimeProvider time, ShortenerOptions options, IMetrics metrics)
    {
        _limiter = limiter;
        _time = time;
        _options = options;
        _metrics = metrics;
    }

    public int Order => 5;

    public Task<IResult?> BeforeAsync(HttpContext context)
    {
        if (!Routes.IsCreateLink(context))
        {
            return Task.FromResult<IResult?>(null);
        }

        var key = ClientKey.For(context, _options);
        if (_limiter.TryAcquire(key, _time.GetUtcNow(), out var retryAfter))
        {
            return Task.FromResult<IResult?>(null);
        }

        _metrics.Increment("rate_limited_total");
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        IResult result = ApiResults.Error(StatusCodes.Status429TooManyRequests, "rate_limited",
            "Too many requests. Retry later.",
            new Dictionary<string, string> { ["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return Task.FromResult<IResult?>(result);
    }
}
