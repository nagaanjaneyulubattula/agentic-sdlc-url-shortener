using UrlShortener.Api.Features.RateLimit;

namespace UrlShortener.Tests.Features.RateLimit;

public class RateLimitTests
{
    private static Dictionary<string, string?> Limits(double capacity, double refillPerSecond, bool trustProxy = false) => new()
    {
        ["RateLimit:Capacity"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RateLimit:RefillPerSecond"] = refillPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Shortener:TrustProxy"] = trustProxy ? "true" : "false",
    };

    private static DateTimeOffset T(double seconds) => TestApp.StartTime.AddSeconds(seconds);

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Requests_beyond_the_capacity_get_429_with_Retry_After()
    {
        using var app = new TestApp(config: Limits(3, 1));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await app.Client.ShortenAsync()).StatusCode);
        }

        var limited = await app.Client.ShortenAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await limited.ErrorCodeAsync());
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Retry_After_reflects_the_refill_rate()
    {
        using var app = new TestApp(config: Limits(1, 0.5));
        await app.Client.ShortenAsync();

        var limited = await app.Client.ShortenAsync();

        Assert.Equal("2", limited.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Limited_requests_do_not_create_links()
    {
        using var app = new TestApp(config: Limits(2, 1));

        for (var i = 0; i < 6; i++)
        {
            await app.Client.ShortenAsync();
        }

        Assert.Equal(2, app.Get<IMetrics>().Snapshot()["links_created_total"]);
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Trailing_slash_and_invalid_bodies_are_limited_too()
    {
        using var app = new TestApp(config: Limits(2, 1));

        await app.Client.PostRawAsync("{}", path: "/api/links/");
        await app.Client.PostRawAsync("not json", path: "/api/links");
        var limited = await app.Client.PostRawAsync("{}", path: "/api/links/");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-2")]
    public async Task Tokens_refill_over_time()
    {
        using var app = new TestApp(config: Limits(3, 1));
        for (var i = 0; i < 3; i++)
        {
            await app.Client.ShortenAsync();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Client.ShortenAsync()).StatusCode);

        app.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.Created, (await app.Client.ShortenAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.ShortenAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Client.ShortenAsync()).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-2")]
    public async Task A_partial_refill_is_not_enough_for_another_request()
    {
        using var app = new TestApp(config: Limits(1, 1));
        await app.Client.ShortenAsync();

        app.Time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Client.ShortenAsync()).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-2")]
    public async Task Refill_never_exceeds_the_bucket_capacity()
    {
        using var app = new TestApp(config: Limits(3, 1));
        await app.Client.ShortenAsync();

        app.Time.Advance(TimeSpan.FromHours(1));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await app.Client.ShortenAsync()).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public async Task Each_client_has_its_own_bucket()
    {
        using var app = new TestApp(config: Limits(2, 1, trustProxy: true));
        var alice = HttpExtensions.From("203.0.113.10");
        var bob = HttpExtensions.From("203.0.113.20");

        await app.Client.ShortenAsync(headers: alice);
        await app.Client.ShortenAsync(headers: alice);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Client.ShortenAsync(headers: alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.ShortenAsync(headers: bob)).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public async Task Forwarded_header_cannot_be_used_to_dodge_the_limit_unless_the_proxy_is_trusted()
    {
        using var app = new TestApp(config: Limits(2, 1, trustProxy: false));

        for (var i = 0; i < 2; i++)
        {
            await app.Client.ShortenAsync(headers: HttpExtensions.From("203.0.113." + i));
        }

        var limited = await app.Client.ShortenAsync(headers: HttpExtensions.From("203.0.113.99"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public async Task Other_routes_such_as_redirects_are_never_rate_limited()
    {
        using var app = new TestApp(config: Limits(1, 0.001));
        await app.Client.ShortenAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Client.ShortenAsync()).StatusCode);

        for (var i = 0; i < 20; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await app.Client.GetAsync("/unknown1")).StatusCode);
        }

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await app.Client.GetAsync("/api/links")).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public void Tracking_map_is_bounded_and_evicts_least_recently_used_keys()
    {
        var limiter = new TokenBucketLimiter(capacity: 1, refillPerSecond: 0, maxKeys: 3);

        for (var i = 0; i < 100; i++)
        {
            limiter.TryAcquire("client-" + i, T(0), out _);
        }

        Assert.Equal(3, limiter.TrackedKeys);
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public void Recently_used_keys_survive_eviction()
    {
        var limiter = new TokenBucketLimiter(capacity: 1, refillPerSecond: 0, maxKeys: 3);
        Assert.True(limiter.TryAcquire("a", T(0), out _));
        Assert.True(limiter.TryAcquire("b", T(0), out _));
        Assert.True(limiter.TryAcquire("c", T(0), out _));
        Assert.False(limiter.TryAcquire("a", T(0), out _));   // touch a: now the most recently used
        Assert.True(limiter.TryAcquire("d", T(0), out _));    // evicts b (least recently used)

        Assert.False(limiter.TryAcquire("a", T(0), out _));   // a is still tracked and empty
        Assert.True(limiter.TryAcquire("b", T(0), out _));    // b was forgotten and starts with a full bucket
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public void Limiter_reports_how_long_until_the_next_token()
    {
        var limiter = new TokenBucketLimiter(capacity: 2, refillPerSecond: 0.25, maxKeys: 10);
        limiter.TryAcquire("k", T(0), out _);
        limiter.TryAcquire("k", T(0), out _);

        TimeSpan retryAfter;
        var allowed = limiter.TryAcquire("k", T(0), out retryAfter);
        Assert.False(allowed);
        Assert.Equal(4.0, retryAfter.TotalSeconds, 3);

        allowed = limiter.TryAcquire("k", T(1), out retryAfter);
        Assert.False(allowed);
        Assert.Equal(3.0, retryAfter.TotalSeconds, 3);

        Assert.True(limiter.TryAcquire("k", T(4), out _));
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public void Limiter_is_safe_under_concurrency_and_never_over_grants()
    {
        var limiter = new TokenBucketLimiter(capacity: 50, refillPerSecond: 0, maxKeys: 10);
        var granted = 0;

        Parallel.For(0, 1_000, attempt =>
        {
            if (limiter.TryAcquire("shared", T(0), out _))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(50, granted);
    }

    [Fact]
    [Trait("AC", "AC-RL-3")]
    public void Defaults_are_capacity_10_refill_1_and_10000_keys()
    {
        var options = new RateLimitOptions();

        Assert.Equal(10, options.Capacity);
        Assert.Equal(1, options.RefillPerSecond);
        Assert.Equal(10_000, options.MaxKeys);
    }
}
