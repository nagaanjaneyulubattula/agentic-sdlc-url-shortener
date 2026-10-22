using System.Text.RegularExpressions;

namespace UrlShortener.Tests.Features.Alias;

public class AliasTests
{
    private const string Url = "https://example.com/promo";

    [Fact]
    [Trait("AC", "AC-ALIAS-1")]
    public async Task Custom_alias_is_used_as_the_short_code()
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, alias = "summer-sale" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.Equal("summer-sale", json.GetProperty("code").GetString());
        Assert.Equal("http://localhost/summer-sale", json.GetProperty("shortUrl").GetString());
        Assert.NotNull(app.Get<ILinkStore>().Get("summer-sale"));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-1")]
    public async Task Alias_link_is_stored_under_the_alias_with_the_original_url()
    {
        using var app = new TestApp();
        await app.Client.PostLinkAsync(new { url = Url, alias = "go_now" });

        var stored = app.Get<ILinkStore>().Get("go_now");

        Assert.NotNull(stored);
        Assert.Equal(Url, stored!.Url);
    }

    [Theory]
    [Trait("AC", "AC-ALIAS-1")]
    [InlineData("abc")]
    [InlineData("A-b_C-9")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("___")]
    public async Task Aliases_at_the_boundaries_of_the_allowed_format_are_accepted(string alias)
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, alias });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(alias, (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-1")]
    public async Task Missing_or_null_alias_falls_back_to_a_generated_code()
    {
        using var app = new TestApp();

        var absent = await (await app.Client.PostLinkAsync(new { url = Url })).JsonAsync();
        var nullAlias = await (await app.Client.PostRawAsync("{\"url\":\"" + Url + "\",\"alias\":null}")).JsonAsync();

        Assert.Matches(new Regex(@"^[0-9A-Za-z]{7}\z"), absent.GetProperty("code").GetString()!);
        Assert.Matches(new Regex(@"^[0-9A-Za-z]{7}\z"), nullAlias.GetProperty("code").GetString()!);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-1")]
    public async Task Aliases_are_case_sensitive()
    {
        using var app = new TestApp();

        var upper = await app.Client.PostLinkAsync(new { url = "https://example.com/upper", alias = "Promo" });
        var lower = await app.Client.PostLinkAsync(new { url = "https://example.com/lower", alias = "promo" });

        Assert.Equal(HttpStatusCode.Created, upper.StatusCode);
        Assert.Equal(HttpStatusCode.Created, lower.StatusCode);
        var store = app.Get<ILinkStore>();
        Assert.Equal("https://example.com/upper", store.Get("Promo")!.Url);
        Assert.Equal("https://example.com/lower", store.Get("promo")!.Url);
        Assert.Null(store.Get("PROMO"));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-2")]
    public async Task Duplicate_alias_returns_409_alias_taken_and_keeps_the_original()
    {
        using var app = new TestApp();
        await app.Client.PostLinkAsync(new { url = "https://example.com/first", alias = "taken" });

        var response = await app.Client.PostLinkAsync(new { url = "https://example.com/second", alias = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("alias_taken", await response.ErrorCodeAsync());
        Assert.Equal("https://example.com/first", app.Get<ILinkStore>().Get("taken")!.Url);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-2")]
    public async Task Concurrent_requests_for_the_same_alias_produce_exactly_one_winner()
    {
        using var app = new TestApp();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => app.Client.PostLinkAsync(new { url = $"https://example.com/{i}", alias = "race-code" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Theory]
    [Trait("AC", "AC-ALIAS-3")]
    [InlineData("api")]
    [InlineData("healthz")]
    [InlineData("readyz")]
    [InlineData("metrics")]
    [InlineData("admin")]
    [InlineData("API")]
    [InlineData("Admin")]
    [InlineData("HealthZ")]
    [InlineData("MeTrIcS")]
    public async Task Reserved_words_are_rejected_regardless_of_case(string alias)
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, alias });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("alias_reserved", await response.ErrorCodeAsync());
    }

    [Theory]
    [Trait("AC", "AC-ALIAS-3")]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("slash/inside")]
    [InlineData("dot.dot")]
    [InlineData("q?x=1")]
    [InlineData("café")]
    [InlineData("abc\n")]
    [InlineData("<script>")]
    [InlineData("..%2f..")]
    public async Task Invalid_aliases_are_rejected_with_400(string alias)
    {
        using var app = new TestApp();

        var response = await app.Client.PostLinkAsync(new { url = Url, alias });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_alias", await response.ErrorCodeAsync());
        Assert.False(app.Get<IMetrics>().Snapshot().ContainsKey("links_created_total"));
    }

    [Theory]
    [Trait("AC", "AC-ALIAS-3")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"abc\"]")]
    public async Task Non_string_alias_is_rejected_with_400(string aliasJson)
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync("{\"url\":\"" + Url + "\",\"alias\":" + aliasJson + "}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_alias", await response.ErrorCodeAsync());
    }
}
