using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using UrlShortener.Api.Features.Shorten;

namespace UrlShortener.Tests.Features.Shorten;

public class ShortenTests
{
    private static readonly Regex CodePattern = new(@"^[0-9A-Za-z]{7}\z");

    [Fact]
    [Trait("AC", "AC-SHORT-1")]
    public async Task Valid_url_returns_201_with_code_shortUrl_url_and_createdAt()
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync("https://example.com/a/b?c=d");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.JsonAsync();
        var code = json.GetProperty("code").GetString()!;
        Assert.Matches(CodePattern, code);
        Assert.Equal($"http://localhost/{code}", json.GetProperty("shortUrl").GetString());
        Assert.Equal("https://example.com/a/b?c=d", json.GetProperty("url").GetString());
        Assert.Equal(TestApp.StartTime, json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-1")]
    public async Task Response_is_json_and_the_link_is_stored()
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync();
        var code = (await response.JsonAsync()).GetProperty("code").GetString()!;

        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        var stored = app.Get<ILinkStore>().Get(code);
        Assert.NotNull(stored);
        Assert.Equal(HttpExtensions.ExampleUrl, stored!.Url);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-1")]
    public async Task ShortUrl_uses_configured_BaseUrl()
    {
        using var app = new TestApp(config: new Dictionary<string, string?> { ["Shortener:BaseUrl"] = "https://sho.rt/" });

        var json = await (await app.Client.ShortenAsync()).JsonAsync();

        Assert.Equal("https://sho.rt/" + json.GetProperty("code").GetString(), json.GetProperty("shortUrl").GetString());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-1")]
    public async Task Surrounding_whitespace_in_url_is_trimmed()
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync("  https://example.com/x  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("https://example.com/x", (await response.JsonAsync()).GetProperty("url").GetString());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-1")]
    public async Task Link_creation_increments_the_links_created_counter()
    {
        using var app = new TestApp();

        await app.Client.ShortenAsync();
        await app.Client.ShortenAsync();

        Assert.Equal(2, app.Get<IMetrics>().Snapshot()["links_created_total"]);
    }

    [Theory]
    [Trait("AC", "AC-SHORT-2")]
    [InlineData("{}")]
    [InlineData("{\"url\":null}")]
    [InlineData("{\"url\":123}")]
    [InlineData("{\"url\":\"\"}")]
    [InlineData("{\"url\":\"   \"}")]
    [InlineData("{\"url\":\"not a url\"}")]
    [InlineData("{\"url\":\"example.com\"}")]
    public async Task Missing_or_unparseable_url_returns_400_invalid_url(string body)
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_url", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task Oversized_url_returns_400_url_too_long()
    {
        using var app = new TestApp();
        var url = "https://example.com/" + new string('a', 2049 - "https://example.com/".Length);

        var response = await app.Client.ShortenAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("url_too_long", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task Url_of_exactly_2048_characters_is_accepted()
    {
        using var app = new TestApp();
        var url = "https://example.com/" + new string('a', 2048 - "https://example.com/".Length);

        var response = await app.Client.ShortenAsync(url);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task Other_methods_on_the_collection_return_a_json_405()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/api/links");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("method_not_allowed", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task Errors_are_machine_readable_json()
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync("{}");

        var error = (await response.JsonAsync()).GetProperty("error");
        Assert.Equal("invalid_url", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public async Task Generated_codes_are_seven_character_base62_and_unique()
    {
        using var app = new TestApp();
        var codes = new HashSet<string>();

        for (var i = 0; i < 300; i++)
        {
            var code = await app.Client.CreateCodeAsync();
            Assert.Matches(CodePattern, code);
            Assert.True(codes.Add(code), $"duplicate code {code}");
        }
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public async Task Code_collision_is_retried_until_a_free_code_is_found()
    {
        var store = new FaultyLinkStore { CollideFirst = 3 };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        var response = await app.Client.ShortenAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(4, store.CreateAttempts);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public async Task Collision_retries_are_bounded()
    {
        var store = new FaultyLinkStore { CollideFirst = 1000 };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        var response = await app.Client.ShortenAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("code_generation_failed", await response.ErrorCodeAsync());
        Assert.Equal(ShortenEndpoint.MaxCodeAttempts, store.CreateAttempts);
    }

    [Theory]
    [Trait("AC", "AC-SHORT-4")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("{\"url\":")]
    [InlineData("")]
    public async Task Malformed_json_returns_400_invalid_json(string body)
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_json", await response.ErrorCodeAsync());
    }

    [Theory]
    [Trait("AC", "AC-SHORT-4")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Json_that_is_not_an_object_returns_400_invalid_request(string body)
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-4")]
    public async Task Body_over_16KB_returns_413_payload_too_large()
    {
        using var app = new TestApp();
        var body = PaddedBody(16384 + 1);

        var response = await app.Client.PostRawAsync(body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-4")]
    public async Task Body_of_exactly_16KB_is_accepted()
    {
        using var app = new TestApp();

        var response = await app.Client.PostRawAsync(PaddedBody(16384));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-4")]
    public async Task Chunked_body_over_16KB_without_content_length_returns_413()
    {
        using var app = new TestApp();
        var bytes = Encoding.UTF8.GetBytes(PaddedBody(20000));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/links") { Content = new StreamContent(new ForwardOnlyStream(bytes)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await app.Client.SendAsync(request);

        Assert.Null(request.Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await response.ErrorCodeAsync());
    }

    private static string PaddedBody(int totalBytes)
    {
        const string prefix = "{\"url\":\"https://example.com/\",\"pad\":\"";
        const string suffix = "\"}";
        return prefix + new string('x', totalBytes - prefix.Length - suffix.Length) + suffix;
    }
}
