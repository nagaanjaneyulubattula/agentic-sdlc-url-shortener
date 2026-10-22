namespace UrlShortener.Tests.Features.Expiry;

public class ExpiryTests
{
    private const string Url = "https://example.com/campaign";

    [Fact]
    [Trait("AC", "AC-EXP-1")]
    public async Task TtlSeconds_sets_expiresAt_on_the_created_link()
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 3600 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.Equal(TestApp.StartTime.AddHours(1), json.GetProperty("expiresAt").GetDateTimeOffset());
        var stored = app.Get<ILinkStore>().Get(json.GetProperty("code").GetString()!);
        Assert.Equal(TestApp.StartTime.AddHours(1), stored!.ExpiresAt);
    }

    [Theory]
    [Trait("AC", "AC-EXP-1")]
    [InlineData(1)]
    [InlineData(31_536_000)]
    public async Task Boundary_ttl_values_are_accepted(long ttl)
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = ttl });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TestApp.StartTime.AddSeconds(ttl), (await response.JsonAsync()).GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    [Trait("AC", "AC-EXP-1")]
    public async Task Without_ttl_the_link_never_expires()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync(Url);

        app.Time.Advance(TimeSpan.FromDays(3650));

        Assert.Equal(HttpStatusCode.Found, (await app.Client.GetAsync("/" + code)).StatusCode);
        Assert.Null(app.Get<ILinkStore>().Get(code)!.ExpiresAt);
    }

    [Fact]
    [Trait("AC", "AC-EXP-1")]
    public async Task Null_ttl_is_treated_as_not_provided()
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync("{\"url\":\"" + Url + "\",\"ttlSeconds\":null}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await response.JsonAsync()).GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    [Trait("AC", "AC-EXP-2")]
    public async Task Link_redirects_until_its_expiry_instant()
    {
        using var app = new TestApp();
        var code = await (await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 60 })).JsonAsync();
        var path = "/" + code.GetProperty("code").GetString();

        app.Time.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal(HttpStatusCode.Found, (await app.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-EXP-2")]
    public async Task Expired_link_returns_410_link_expired_without_a_Location()
    {
        using var app = new TestApp();
        var json = await (await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 60 })).JsonAsync();
        var path = "/" + json.GetProperty("code").GetString();

        app.Time.Advance(TimeSpan.FromSeconds(61));
        var response = await app.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("link_expired", await response.ErrorCodeAsync());
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    [Trait("AC", "AC-EXP-2")]
    public async Task Link_is_expired_at_exactly_the_expiry_instant()
    {
        using var app = new TestApp();
        var json = await (await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 60 })).JsonAsync();
        var path = "/" + json.GetProperty("code").GetString();

        app.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(HttpStatusCode.Gone, (await app.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-EXP-2")]
    public async Task Same_link_flips_from_302_to_410_as_the_injected_clock_advances()
    {
        using var app = new TestApp();
        var json = await (await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 10 })).JsonAsync();
        var path = "/" + json.GetProperty("code").GetString();

        var before = await app.Client.GetAsync(path);
        app.Time.Advance(TimeSpan.FromSeconds(11));
        var after = await app.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, before.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-EXP-2")]
    public async Task Expired_redirects_do_not_count_as_successful_redirects()
    {
        using var app = new TestApp();
        var json = await (await app.Client.PostLinkAsync(new { url = Url, ttlSeconds = 5 })).JsonAsync();
        var path = "/" + json.GetProperty("code").GetString();

        await app.Client.GetAsync(path);
        app.Time.Advance(TimeSpan.FromSeconds(6));
        await app.Client.GetAsync(path);

        Assert.Equal(1, app.Get<IMetrics>().Snapshot()["redirects_total"]);
    }

    [Theory]
    [Trait("AC", "AC-EXP-3")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-3600")]
    [InlineData("1.5")]
    [InlineData("60.0")]
    [InlineData("31536001")]
    [InlineData("99999999999999999999")]
    [InlineData("\"60\"")]
    [InlineData("true")]
    [InlineData("[60]")]
    [InlineData("{}")]
    public async Task Invalid_ttl_returns_400_invalid_ttl_and_creates_nothing(string ttlJson)
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync("{\"url\":\"" + Url + "\",\"ttlSeconds\":" + ttlJson + "}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_ttl", await response.ErrorCodeAsync());
        Assert.False(app.Get<IMetrics>().Snapshot().ContainsKey("links_created_total"));
    }
}
