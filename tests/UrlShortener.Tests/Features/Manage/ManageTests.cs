namespace UrlShortener.Tests.Features.Manage;

public class ManageTests
{
    [Fact]
    [Trait("AC", "AC-MANAGE-1")]
    public async Task Get_returns_link_metadata()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync("https://example.com/meta");

        var response = await app.Client.GetAsync("/api/links/" + code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal("https://example.com/meta", json.GetProperty("url").GetString());
        Assert.Equal($"http://localhost/{code}", json.GetProperty("shortUrl").GetString());
        Assert.Equal(TestApp.StartTime, json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-1")]
    public async Task Get_unknown_code_returns_404()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/api/links/nope123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-2")]
    public async Task Delete_returns_204_and_the_link_no_longer_redirects()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();
        Assert.Equal(HttpStatusCode.Found, (await app.Client.GetAsync("/" + code)).StatusCode);

        var response = await app.Client.DeleteAsync("/api/links/" + code);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/links/" + code)).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-3")]
    public async Task Delete_unknown_code_returns_404()
    {
        using var app = new TestApp();

        var response = await app.Client.DeleteAsync("/api/links/nope123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-3")]
    public async Task Deleting_twice_returns_404_the_second_time()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/links/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/links/" + code)).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-2")]
    public async Task Delete_only_removes_the_requested_link()
    {
        using var app = new TestApp();
        var first = await app.Client.CreateCodeAsync("https://example.com/one");
        var second = await app.Client.CreateCodeAsync("https://example.com/two");

        await app.Client.DeleteAsync("/api/links/" + first);

        Assert.Equal(HttpStatusCode.Found, (await app.Client.GetAsync("/" + second)).StatusCode);
    }
}
