using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Features.Analytics;

namespace UrlShortener.Tests.Features.Analytics;

public class AnalyticsTests
{
    private static readonly Dictionary<string, string?> TrustProxy = new() { ["Shortener:TrustProxy"] = "true" };

    private static async Task<JsonElement> StatsAsync(TestApp app, string code, string query = "")
    {
        var response = await app.Client.GetAsync($"/api/links/{code}/stats{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static Task<HttpResponseMessage> ClickAsync(TestApp app, string code, string? referer = null, string? client = null)
    {
        var headers = client is null ? new Dictionary<string, string>() : HttpExtensions.From(client);
        if (referer is not null)
        {
            headers["Referer"] = referer;
        }

        return app.Client.GetWithAsync("/" + code, headers);
    }

    // ------------------------------------------------------------------ AC-ANA-1 / AC-ANA-2

    [Fact]
    [Trait("AC", "AC-ANA-1")]
    public async Task Every_successful_redirect_increments_totalClicks()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Found, (await ClickAsync(app, code)).StatusCode);
        }

        Assert.Equal(5, (await StatsAsync(app, code)).GetProperty("totalClicks").GetInt64());
    }

    [Fact]
    [Trait("AC", "AC-ANA-1")]
    public async Task Clicks_are_tracked_per_link()
    {
        using var app = new TestApp();
        var first = await app.Client.CreateCodeAsync("https://example.com/1");
        var second = await app.Client.CreateCodeAsync("https://example.com/2");

        await ClickAsync(app, first);
        await ClickAsync(app, first);
        await ClickAsync(app, second);

        Assert.Equal(2, (await StatsAsync(app, first)).GetProperty("totalClicks").GetInt64());
        Assert.Equal(1, (await StatsAsync(app, second)).GetProperty("totalClicks").GetInt64());
    }

    [Fact]
    [Trait("AC", "AC-ANA-1")]
    public async Task Unknown_codes_are_not_counted_and_do_not_break_anything()
    {
        using var app = new TestApp();

        var response = await ClickAsync(app, "nope123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, app.Get<IClickStore>().Snapshot("nope123").TotalClicks);
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task Stats_have_the_documented_shape()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();
        await ClickAsync(app, code, referer: "https://news.example.org/story");

        var stats = await StatsAsync(app, code);

        Assert.Equal(code, stats.GetProperty("code").GetString());
        Assert.Equal(1, stats.GetProperty("totalClicks").GetInt64());
        Assert.Equal(1, stats.GetProperty("uniqueVisitors").GetInt32());
        var day = stats.GetProperty("clicksByDay").EnumerateArray().Single();
        Assert.Equal("2030-01-01", day.GetProperty("date").GetString());
        Assert.Equal(1, day.GetProperty("clicks").GetInt32());
        var referrer = stats.GetProperty("topReferrers").EnumerateArray().Single();
        Assert.Equal("news.example.org", referrer.GetProperty("host").GetString());
        Assert.Equal(1, referrer.GetProperty("clicks").GetInt32());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task Link_without_clicks_has_zero_totals_and_empty_lists()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        var stats = await StatsAsync(app, code);

        Assert.Equal(0, stats.GetProperty("totalClicks").GetInt64());
        Assert.Equal(0, stats.GetProperty("uniqueVisitors").GetInt32());
        Assert.Empty(stats.GetProperty("clicksByDay").EnumerateArray());
        Assert.Empty(stats.GetProperty("topReferrers").EnumerateArray());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task Stats_for_an_unknown_code_return_404()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/api/links/nope123/stats");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task UniqueVisitors_counts_distinct_clients()
    {
        using var app = new TestApp(config: TrustProxy);
        var code = await app.Client.CreateCodeAsync();

        await ClickAsync(app, code, client: "203.0.113.1");
        await ClickAsync(app, code, client: "203.0.113.1");
        await ClickAsync(app, code, client: "203.0.113.2");

        var stats = await StatsAsync(app, code);
        Assert.Equal(3, stats.GetProperty("totalClicks").GetInt64());
        Assert.Equal(2, stats.GetProperty("uniqueVisitors").GetInt32());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task ClicksByDay_groups_by_utc_date_in_ascending_order()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        await ClickAsync(app, code);
        app.Time.Advance(TimeSpan.FromDays(1));
        await ClickAsync(app, code);
        await ClickAsync(app, code);

        var days = (await StatsAsync(app, code)).GetProperty("clicksByDay").EnumerateArray().ToList();
        Assert.Equal(2, days.Count);
        Assert.Equal("2030-01-01", days[0].GetProperty("date").GetString());
        Assert.Equal(1, days[0].GetProperty("clicks").GetInt32());
        Assert.Equal("2030-01-02", days[1].GetProperty("date").GetString());
        Assert.Equal(2, days[1].GetProperty("clicks").GetInt32());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task Days_parameter_limits_the_window_but_not_the_total()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();
        await ClickAsync(app, code);
        app.Time.Advance(TimeSpan.FromDays(10));
        await ClickAsync(app, code);

        var week = await StatsAsync(app, code, "?days=7");
        var month = await StatsAsync(app, code, "?days=30");

        Assert.Single(week.GetProperty("clicksByDay").EnumerateArray());
        Assert.Equal(2, week.GetProperty("totalClicks").GetInt64());
        Assert.Equal(2, month.GetProperty("clicksByDay").GetArrayLength());
    }

    [Theory]
    [Trait("AC", "AC-ANA-2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("91")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public async Task Invalid_days_returns_400(string days)
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        var response = await app.Client.GetAsync($"/api/links/{code}/stats?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_days", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-ANA-2")]
    public async Task TopReferrers_are_host_only_lowercase_ordered_by_clicks_and_ignore_missing_or_invalid_referers()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        await ClickAsync(app, code, "https://Blog.Example.NET/a/b?secret=1");
        await ClickAsync(app, code, "https://news.example.org/post/1?utm=x");
        await ClickAsync(app, code, "https://news.example.org/post/2");
        await ClickAsync(app, code, "not a url");
        await ClickAsync(app, code, "ftp://files.example.com/x");
        await ClickAsync(app, code);

        var stats = await StatsAsync(app, code);
        var referrers = stats.GetProperty("topReferrers").EnumerateArray()
            .Select(r => (r.GetProperty("host").GetString()!, r.GetProperty("clicks").GetInt32())).ToList();
        Assert.Equal(new[] { ("news.example.org", 2), ("blog.example.net", 1) }, referrers);
        Assert.Equal(6, stats.GetProperty("totalClicks").GetInt64());
        Assert.DoesNotContain("secret", stats.ToString());
    }

    [Theory]
    [Trait("AC", "AC-ANA-2")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("not a url", null)]
    [InlineData("/relative", null)]
    [InlineData("ftp://example.com/", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://Example.COM/Path?q=1", "example.com")]
    [InlineData("http://sub.example.org:8080/", "sub.example.org")]
    public void ParseReferrerHost_returns_only_the_lowercase_host_of_http_urls(string? referer, string? expected)
    {
        Assert.Equal(expected, AnalyticsRedirectHook.ParseReferrerHost(referer));
    }

    // ------------------------------------------------------------------ AC-ANA-3 privacy

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public async Task Raw_client_addresses_are_never_stored_or_returned()
    {
        const string address = "203.0.113.77";
        using var app = new TestApp(config: TrustProxy);
        var code = await app.Client.CreateCodeAsync();

        await ClickAsync(app, code, client: address);

        var stats = await app.Client.GetAsync($"/api/links/{code}/stats");
        Assert.DoesNotContain(address, await stats.Content.ReadAsStringAsync());
        var events = app.Get<IClickStore>().Snapshot(code).Events;
        var click = Assert.Single(events);
        Assert.DoesNotContain(address, click.ToString());
        Assert.Equal(ClientKey.ForAddress(address, "test-salt-0123456789"), click.ClientKey);
        Assert.Matches(new Regex(@"^[0-9a-f]{16}\z"), click.ClientKey);
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Analytics_types_have_no_field_or_property_that_could_hold_an_address()
    {
        var forbidden = new[] { "ip", "ipaddress", "remoteip", "remoteipaddress", "clientip", "address", "forwardedfor" };
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = typeof(AnalyticsRedirectHook).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AnalyticsRedirectHook).Namespace)
            .SelectMany(t => t.GetFields(flags).Select(f => f.Name).Concat(t.GetProperties(flags).Select(p => p.Name)))
            .Select(n => n.Trim('_').ToLowerInvariant())
            .ToList();

        Assert.NotEmpty(members);
        Assert.Empty(members.Intersect(forbidden));
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Only_ClientKey_reads_the_connection_address_or_forwarded_header()
    {
        // Built from parts so this test file itself does not contain the literals.
        var needles = new[] { "Remote" + "IpAddress", "X-Forwarded" + "-For" };
        var offenders = RepoPaths.ApiSourceFiles()
            .Where(f => Path.GetFileName(f) != "ClientKey.cs")
            .Where(f => needles.Any(n => File.ReadAllText(f).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------------ AC-ANA-4 failure isolation

    [Fact]
    [Trait("AC", "AC-ANA-4")]
    public async Task Analytics_storage_failure_never_breaks_the_redirect()
    {
        using var app = new TestApp(s =>
        {
            s.RemoveAll<IClickStore>();
            s.AddSingleton<IClickStore>(new ExplodingClickStore());
        });
        var code = await app.Client.CreateCodeAsync("https://example.com/still-works");

        var response = await ClickAsync(app, code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/still-works", response.Headers.Location!.OriginalString);
    }

    [Fact]
    [Trait("AC", "AC-ANA-4")]
    public async Task Redirects_keep_working_repeatedly_while_analytics_is_failing()
    {
        using var app = new TestApp(s =>
        {
            s.RemoveAll<IClickStore>();
            s.AddSingleton<IClickStore>(new ExplodingClickStore());
        });
        var code = await app.Client.CreateCodeAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Found, (await ClickAsync(app, code)).StatusCode);
        }

        Assert.Equal(5, app.Get<IMetrics>().Snapshot()["redirects_total"]);
    }

    // ------------------------------------------------------------------ AC-ANA-5 bounded storage

    [Fact]
    [Trait("AC", "AC-ANA-5")]
    public void Event_storage_is_capped_at_10000_while_the_total_stays_exact()
    {
        var store = new InMemoryClickStore(Options.Create(new AnalyticsOptions()));
        var start = TestApp.StartTime;

        for (var i = 0; i < 10_050; i++)
        {
            store.Record("hot", new ClickEvent(start.AddSeconds(i), null, "client" + i));
        }

        var snapshot = store.Snapshot("hot");
        Assert.Equal(10_050, snapshot.TotalClicks);
        Assert.Equal(10_000, snapshot.Events.Count);
        Assert.Equal("client50", snapshot.Events[0].ClientKey);
        Assert.Equal("client10049", snapshot.Events[^1].ClientKey);
    }

    [Fact]
    [Trait("AC", "AC-ANA-5")]
    public async Task Stats_totals_stay_accurate_when_the_event_buffer_wraps()
    {
        using var app = new TestApp(config: new Dictionary<string, string?> { ["Analytics:MaxEventsPerLink"] = "5" });
        var code = await app.Client.CreateCodeAsync();

        for (var i = 0; i < 8; i++)
        {
            await ClickAsync(app, code);
        }

        var stats = await StatsAsync(app, code);
        Assert.Equal(8, stats.GetProperty("totalClicks").GetInt64());
        Assert.Equal(5, stats.GetProperty("clicksByDay").EnumerateArray().Sum(d => d.GetProperty("clicks").GetInt32()));
        Assert.Equal(5, app.Get<IClickStore>().Snapshot(code).Events.Count);
    }

    [Fact]
    [Trait("AC", "AC-ANA-5")]
    public void Events_older_than_the_retention_period_are_pruned_but_still_counted()
    {
        var store = new InMemoryClickStore(Options.Create(new AnalyticsOptions { RetentionDays = 90 }));
        var start = TestApp.StartTime;

        store.Record("old", new ClickEvent(start, null, "a"));
        store.Record("old", new ClickEvent(start.AddDays(91), null, "b"));

        var snapshot = store.Snapshot("old");
        Assert.Equal(2, snapshot.TotalClicks);
        Assert.Equal("b", Assert.Single(snapshot.Events).ClientKey);
    }

    [Fact]
    [Trait("AC", "AC-ANA-5")]
    public async Task Concurrent_clicks_are_all_counted()
    {
        var store = new InMemoryClickStore(Options.Create(new AnalyticsOptions()));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                store.Record("busy", new ClickEvent(TestApp.StartTime, null, "t" + t));
            }
        })));

        Assert.Equal(4_000, store.Snapshot("busy").TotalClicks);
    }

    private sealed class ExplodingClickStore : IClickStore
    {
        public void Record(string code, ClickEvent click) => throw new InvalidOperationException("analytics storage is down");

        public ClickSnapshot Snapshot(string code) => throw new InvalidOperationException("analytics storage is down");
    }
}
