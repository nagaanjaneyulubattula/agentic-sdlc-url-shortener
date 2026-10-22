using UrlShortener.Api.Features.Idempotency;

namespace UrlShortener.Tests.Features.Idempotency;

public class IdempotencyTests
{
    private const string Header = "Idempotency-Key";

    private static Dictionary<string, string> Key(string key) => new() { [Header] = key };

    private static long Created(TestApp app) =>
        app.Get<IMetrics>().Snapshot().GetValueOrDefault("links_created_total");

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Same_key_and_body_replays_the_original_response_without_creating_a_second_link()
    {
        using var app = new TestApp();

        var first = await app.Client.ShortenAsync(headers: Key("abc-123"));
        var second = await app.Client.ShortenAsync(headers: Key("abc-123"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replay"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Replay_keeps_returning_the_same_code_for_every_retry()
    {
        using var app = new TestApp();
        var codes = new HashSet<string>();

        for (var i = 0; i < 5; i++)
        {
            var response = await app.Client.ShortenAsync(headers: Key("retry-me"));
            codes.Add((await response.JsonAsync()).GetProperty("code").GetString()!);
        }

        Assert.Single(codes);
        Assert.Equal(1, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Requests_without_a_key_are_never_deduplicated()
    {
        using var app = new TestApp();

        var first = await (await app.Client.ShortenAsync()).JsonAsync();
        var second = await (await app.Client.ShortenAsync()).JsonAsync();

        Assert.NotEqual(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        Assert.Equal(2, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Different_keys_create_different_links()
    {
        using var app = new TestApp();

        var first = await (await app.Client.ShortenAsync(headers: Key("one"))).JsonAsync();
        var second = await (await app.Client.ShortenAsync(headers: Key("two"))).JsonAsync();

        Assert.NotEqual(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task A_failed_first_attempt_does_not_poison_the_key()
    {
        using var app = new TestApp();

        var bad = await app.Client.PostRawAsync("{\"url\":\"nonsense\"}", Key("k1"));
        var good = await app.Client.ShortenAsync(headers: Key("k1"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.False(good.Headers.Contains("Idempotent-Replay"));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Keys_are_scoped_per_client()
    {
        using var app = new TestApp(config: new Dictionary<string, string?> { ["Shortener:TrustProxy"] = "true" });
        var alice = new Dictionary<string, string> { [Header] = "shared-key", [ClientKey.ForwardedForHeader] = "203.0.113.1" };
        var bob = new Dictionary<string, string> { [Header] = "shared-key", [ClientKey.ForwardedForHeader] = "203.0.113.2" };

        var first = await app.Client.ShortenAsync(headers: alice);
        var second = await app.Client.ShortenAsync(headers: bob);

        Assert.False(second.Headers.Contains("Idempotent-Replay"));
        Assert.NotEqual((await first.JsonAsync()).GetProperty("code").GetString(), (await second.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("AC", "AC-IDEM-1")]
    public async Task Concurrent_requests_with_one_key_create_exactly_one_link()
    {
        using var app = new TestApp();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => app.Client.ShortenAsync(headers: Key("parallel"))));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        Assert.Equal(1, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-2")]
    public async Task Same_key_with_a_different_body_returns_422_idempotency_key_reuse()
    {
        using var app = new TestApp();
        await app.Client.ShortenAsync("https://example.com/one", Key("dup"));

        var response = await app.Client.ShortenAsync("https://example.com/two", Key("dup"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("idempotency_key_reuse", await response.ErrorCodeAsync());
        Assert.Equal(1, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-2")]
    public async Task Whitespace_only_differences_in_the_body_count_as_a_different_request()
    {
        using var app = new TestApp();
        await app.Client.PostRawAsync("{\"url\":\"https://example.com/a\"}", Key("ws"));

        var response = await app.Client.PostRawAsync("{ \"url\": \"https://example.com/a\" }", Key("ws"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-2")]
    public async Task Key_reuse_is_still_detected_after_a_successful_replay()
    {
        using var app = new TestApp();
        await app.Client.ShortenAsync("https://example.com/a", Key("again"));
        await app.Client.ShortenAsync("https://example.com/a", Key("again"));

        var response = await app.Client.ShortenAsync("https://example.com/b", Key("again"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public async Task Keys_expire_after_24_hours()
    {
        using var app = new TestApp();
        var first = await (await app.Client.ShortenAsync(headers: Key("daily"))).JsonAsync();

        app.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        var stillReplayed = await app.Client.ShortenAsync(headers: Key("daily"));
        app.Time.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = await app.Client.ShortenAsync(headers: Key("daily"));

        Assert.Equal("true", stillReplayed.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(HttpStatusCode.Created, afterExpiry.StatusCode);
        Assert.False(afterExpiry.Headers.Contains("Idempotent-Replay"));
        Assert.NotEqual(first.GetProperty("code").GetString(), (await afterExpiry.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public async Task An_expired_key_can_be_reused_with_a_different_body()
    {
        using var app = new TestApp();
        await app.Client.ShortenAsync("https://example.com/old", Key("recycle"));

        app.Time.Advance(TimeSpan.FromHours(25));
        var response = await app.Client.ShortenAsync("https://example.com/new", Key("recycle"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public void Key_store_is_bounded()
    {
        var store = new IdempotencyStore(TimeSpan.FromHours(24), maxEntries: 5);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(IdempotencyOutcome.Started, store.Begin("k" + i, "fp", TestApp.StartTime, out var reservation, out _));
            store.Complete(reservation!, 201, "{}");
        }

        Assert.Equal(5, store.Count);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public void Oldest_keys_are_evicted_first()
    {
        var store = new IdempotencyStore(TimeSpan.FromHours(24), maxEntries: 2);
        foreach (var key in new[] { "a", "b", "c" })
        {
            store.Begin(key, "fp", TestApp.StartTime, out var reservation, out _);
            store.Complete(reservation!, 201, "{}");
        }

        Assert.Equal(IdempotencyOutcome.Started, store.Begin("a", "fp", TestApp.StartTime, out _, out _));
        Assert.Equal(IdempotencyOutcome.Replay, store.Begin("c", "fp", TestApp.StartTime, out _, out _));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public void Abandoned_reservations_cannot_grow_the_store_without_bound()
    {
        var store = new IdempotencyStore(TimeSpan.FromHours(24), maxEntries: 10);

        for (var i = 0; i < 1_000; i++)
        {
            store.Begin("k" + i, "fp", TestApp.StartTime, out var reservation, out _);
            store.Abandon(reservation!);
        }

        Assert.Equal(0, store.Count);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public void Expired_entries_are_purged()
    {
        var store = new IdempotencyStore();
        store.Begin("k", "fp", TestApp.StartTime, out var reservation, out _);
        store.Complete(reservation!, 201, "{}");

        store.Begin("other", "fp", TestApp.StartTime.AddHours(24), out _, out _);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-3")]
    public void Defaults_are_24_hours_and_10000_entries()
    {
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyStore.DefaultTtl);
        Assert.Equal(10_000, IdempotencyStore.DefaultMaxEntries);
    }

    [Theory]
    [Trait("AC", "AC-IDEM-4")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_key_is_rejected_with_400_invalid_idempotency_key(string key)
    {
        // HttpClient/TestServer drop empty header values, so the gate is exercised directly (as Kestrel would deliver it).
        var gate = new IdempotencyGate(new IdempotencyStore(), TimeProvider.System, new ShortenerOptions());
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/links";
        context.Request.Headers[Header] = key;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());

        var error = await Assert.ThrowsAsync<ApiException>(() => gate.BeforeAsync(context));

        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_idempotency_key", error.Code);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-4")]
    public async Task Repeated_key_headers_are_rejected()
    {
        using var app = new TestApp();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = new StringContent("{\"url\":\"https://example.com/\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(Header, new[] { "one", "two" });

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_idempotency_key", await response.ErrorCodeAsync());
        Assert.Equal(0, Created(app));
    }

    [Fact]
    [Trait("AC", "AC-IDEM-4")]
    public async Task Key_longer_than_255_characters_returns_400()
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync(headers: Key(new string('k', 256)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_idempotency_key", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-IDEM-4")]
    public async Task Key_of_exactly_255_characters_is_accepted()
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync(headers: Key(new string('k', 255)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Trait("AC", "AC-IDEM-4")]
    public async Task Idempotency_header_is_ignored_on_other_routes()
    {
        using var app = new TestApp();

        var response = await app.Client.GetWithAsync("/healthz-not-here", Key(string.Empty));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
