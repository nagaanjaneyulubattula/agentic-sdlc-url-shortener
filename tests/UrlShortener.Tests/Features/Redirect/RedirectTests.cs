namespace UrlShortener.Tests.Features.Redirect;

public class RedirectTests
{
    [Theory]
    [Trait("AC", "AC-REDIR-1")]
    [InlineData("https://example.com/a?b=1&c=2")]
    [InlineData("https://example.com")]
    [InlineData("http://example.org:8080/path#frag")]
    public async Task Redirect_returns_302_with_the_original_url_in_Location(string url)
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync(url);

        var response = await app.Client.GetAsync("/" + code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(url, response.Headers.Location!.OriginalString);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-1")]
    public async Task Redirect_is_not_cacheable_and_sends_no_referrer()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        var response = await app.Client.GetAsync("/" + code);

        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    [Trait("AC", "AC-REDIR-1")]
    public async Task Redirect_percent_encodes_non_ascii_urls_for_the_Location_header()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync("https://example.com/päth");

        var response = await app.Client.GetAsync("/" + code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/p%C3%A4th", response.Headers.Location!.OriginalString);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-1")]
    public async Task Redirect_increments_the_redirects_counter()
    {
        using var app = new TestApp();
        var code = await app.Client.CreateCodeAsync();

        await app.Client.GetAsync("/" + code);
        await app.Client.GetAsync("/" + code);

        Assert.Equal(2, app.Get<IMetrics>().Snapshot()["redirects_total"]);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-2")]
    public async Task Unknown_code_returns_404_with_json_error()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/doesNotExist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-REDIR-2")]
    public async Task Codes_are_case_sensitive()
    {
        using var app = new TestApp();
        app.Get<ILinkStore>().TryCreate(new Link("AbCdEfG", "https://example.com/", TestApp.StartTime, null));

        Assert.Equal(HttpStatusCode.Found, (await app.Client.GetAsync("/AbCdEfG")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/abcdefg")).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-3")]
    public async Task Failing_afterRedirect_hook_never_breaks_the_redirect()
    {
        var hook = new ThrowingAfterHook();
        using var app = new TestApp(s => s.AddSingleton<IRedirectHook>(hook));
        var code = await app.Client.CreateCodeAsync("https://example.com/target");

        var response = await app.Client.GetAsync("/" + code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/target", response.Headers.Location!.OriginalString);
        Assert.Equal(1, hook.Calls);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-3")]
    public async Task One_failing_afterRedirect_hook_does_not_stop_the_next_hook()
    {
        var failing = new ThrowingAfterHook { Order = 1 };
        var healthy = new CountingAfterHook { Order = 2 };
        using var app = new TestApp(s =>
        {
            s.AddSingleton<IRedirectHook>(failing);
            s.AddSingleton<IRedirectHook>(healthy);
        });
        var code = await app.Client.CreateCodeAsync();

        var response = await app.Client.GetAsync("/" + code);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-3")]
    public async Task BeforeRedirect_hook_can_stop_the_redirect_with_an_api_error()
    {
        using var app = new TestApp(s => s.AddSingleton<IRedirectHook>(new BlockingBeforeHook()));
        var code = await app.Client.CreateCodeAsync();

        var response = await app.Client.GetAsync("/" + code);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("blocked_by_test", await response.ErrorCodeAsync());
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    [Trait("AC", "AC-REDIR-2")]
    public async Task Store_failure_returns_a_generic_500_without_leaking_details()
    {
        var store = new FaultyLinkStore { ThrowOnGet = true };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        var response = await app.Client.GetAsync("/abc1234");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("internal_error", text);
        Assert.DoesNotContain("injected", text);
        Assert.DoesNotContain("InvalidOperationException", text);
    }

    private sealed class ThrowingAfterHook : IRedirectHook
    {
        public int Order { get; init; }

        public int Calls { get; private set; }

        public Task AfterAsync(RedirectContext context)
        {
            Calls++;
            throw new InvalidOperationException("boom from hook");
        }
    }

    private sealed class CountingAfterHook : IRedirectHook
    {
        public int Order { get; init; }

        public int Calls { get; private set; }

        public Task AfterAsync(RedirectContext context)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingBeforeHook : IRedirectHook
    {
        public int Order => 1;

        public Task BeforeAsync(RedirectContext context) =>
            throw new ApiException(403, "blocked_by_test", "blocked");
    }
}
