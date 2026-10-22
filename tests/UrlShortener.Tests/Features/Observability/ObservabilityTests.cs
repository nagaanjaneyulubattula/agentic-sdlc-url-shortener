namespace UrlShortener.Tests.Features.Observability;

public class ObservabilityTests
{
    [Fact]
    [Trait("AC", "AC-OBS-1")]
    public async Task Healthz_returns_200_with_status_ok()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ok", (await response.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    [Trait("AC", "AC-OBS-1")]
    public async Task Healthz_keeps_working_even_when_the_store_is_down()
    {
        var store = new FaultyLinkStore { ThrowOnPing = true, ThrowOnGet = true };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        var response = await app.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-OBS-2")]
    public async Task Readyz_returns_200_when_the_store_is_reachable()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", (await response.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    [Trait("AC", "AC-OBS-2")]
    public async Task Readyz_returns_503_unavailable_when_the_store_ping_fails()
    {
        var store = new FaultyLinkStore { ThrowOnPing = true };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        var response = await app.Client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("unavailable", JsonDocument.Parse(text).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("unreachable", text);
    }

    [Fact]
    [Trait("AC", "AC-OBS-2")]
    public async Task Readyz_recovers_when_the_store_comes_back()
    {
        var store = new FaultyLinkStore { ThrowOnPing = true };
        using var app = new TestApp(s => s.Replace(ServiceDescriptor.Singleton<ILinkStore>(store)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.GetAsync("/readyz")).StatusCode);
        store.ThrowOnPing = false;

        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/readyz")).StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public async Task Metrics_exposes_creation_and_redirect_counters()
    {
        using var app = new TestApp();
        var metrics = app.Get<IMetrics>();
        metrics.Increment("links_created_total", 3);
        metrics.Increment("redirects_total", 2);

        var response = await app.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.Equal(3, json.GetProperty("links_created_total").GetInt64());
        Assert.Equal(2, json.GetProperty("redirects_total").GetInt64());
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public async Task Metrics_exposes_request_counters_by_status_class()
    {
        using var app = new TestApp();

        await app.Client.GetAsync("/healthz");
        await app.Client.GetAsync("/healthz");
        await app.Client.GetAsync("/no/such/route");
        var json = await (await app.Client.GetAsync("/metrics")).JsonAsync();

        Assert.Equal(3, json.GetProperty("requests_total").GetInt64());
        Assert.Equal(2, json.GetProperty("requests_2xx").GetInt64());
        Assert.Equal(1, json.GetProperty("requests_4xx").GetInt64());
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public async Task Metrics_is_a_flat_object_of_numbers()
    {
        using var app = new TestApp();
        await app.Client.GetAsync("/healthz");

        var json = await (await app.Client.GetAsync("/metrics")).JsonAsync();

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.All(json.EnumerateObject(), p => Assert.Equal(JsonValueKind.Number, p.Value.ValueKind));
    }
}
