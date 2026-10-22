using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace UrlShortener.Tests.Core;

public class UrlValidatorBasicsTests
{
    [Theory]
    [Trait("AC", "AC-SHORT-1")]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/path/to/page?x=1&y=2#frag")]
    [InlineData("https://sub.domain.example.co.uk:8443/a")]
    [InlineData("HTTPS://EXAMPLE.COM/UPPER")]
    [InlineData("https://8.8.8.8/dns")]
    public void Accepts_ordinary_public_http_and_https_urls(string url)
    {
        var (ok, code) = UrlValidator.Validate(url);

        Assert.True(ok, $"expected {url} to be accepted but got {code}");
        Assert.Null(code);
    }

    [Theory]
    [Trait("AC", "AC-SHORT-2")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("example.com")]
    public void Rejects_empty_and_non_absolute_urls_with_a_machine_readable_code(string? url)
    {
        var (ok, code) = UrlValidator.Validate(url);

        Assert.False(ok);
        Assert.Equal("invalid_url", code);
    }
}

public class CodeGeneratorTests
{
    private static readonly Regex Base62 = new(@"^[0-9A-Za-z]+\z");

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public void Default_code_is_seven_base62_characters()
    {
        var code = new CodeGenerator().Generate();

        Assert.Equal(7, code.Length);
        Assert.Matches(Base62, code);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public void Codes_are_unique_across_many_generations()
    {
        var generator = new CodeGenerator();

        var codes = Enumerable.Range(0, 20_000).Select(_ => generator.Generate()).ToHashSet();

        Assert.Equal(20_000, codes.Count);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public void Every_alphabet_character_is_eventually_used()
    {
        var generator = new CodeGenerator();

        var seen = string.Concat(Enumerable.Range(0, 2_000).Select(_ => generator.Generate())).ToHashSet();

        Assert.Equal(62, seen.Count);
    }

    [Theory]
    [Trait("AC", "AC-SHORT-3")]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(64)]
    public void Length_is_configurable(int length)
    {
        Assert.Equal(length, new CodeGenerator().Generate(length).Length);
    }

    [Theory]
    [Trait("AC", "AC-SHORT-3")]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(65)]
    public void Invalid_lengths_are_rejected(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeGenerator().Generate(length));
    }
}

public class InMemoryLinkStoreTests
{
    private static Link NewLink(string code, string url = "https://example.com/") => new(code, url, TestApp.StartTime, null);

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public void TryCreate_fails_on_conflict_and_keeps_the_original()
    {
        var store = new InMemoryLinkStore();

        Assert.True(store.TryCreate(NewLink("abc", "https://example.com/first")));
        Assert.False(store.TryCreate(NewLink("abc", "https://example.com/second")));

        Assert.Equal("https://example.com/first", store.Get("abc")!.Url);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-3")]
    public void TryCreate_is_atomic_under_concurrency()
    {
        var store = new InMemoryLinkStore();
        var winners = 0;

        Parallel.For(0, 2_000, i =>
        {
            if (store.TryCreate(NewLink("contested", "https://example.com/" + i)))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-2")]
    public void Get_returns_null_for_unknown_codes_and_is_case_sensitive()
    {
        var store = new InMemoryLinkStore();
        store.TryCreate(NewLink("AbC"));

        Assert.Null(store.Get("missing"));
        Assert.Null(store.Get("abc"));
        Assert.NotNull(store.Get("AbC"));
    }

    [Fact]
    [Trait("AC", "AC-MANAGE-2")]
    public void Delete_removes_the_link_and_reports_whether_it_existed()
    {
        var store = new InMemoryLinkStore();
        store.TryCreate(NewLink("abc"));

        Assert.True(store.Delete("abc"));
        Assert.False(store.Delete("abc"));
        Assert.Null(store.Get("abc"));
    }

    [Fact]
    [Trait("AC", "AC-OBS-2")]
    public void Ping_succeeds_for_the_in_memory_store()
    {
        new InMemoryLinkStore().Ping();
    }
}

public class ClientKeyTests
{
    private const string Salt = "unit-test-salt";

    private static ShortenerOptions Options(bool trustProxy) => new() { AnalyticsSalt = Salt, TrustProxy = trustProxy };

    private static DefaultHttpContext WithForwardedFor(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ClientKey.ForwardedForHeader] = value;
        return context;
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Key_is_sixteen_lowercase_hex_characters()
    {
        Assert.Matches(new Regex(@"^[0-9a-f]{16}\z"), ClientKey.ForAddress("203.0.113.7", Salt));
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Key_never_contains_the_address()
    {
        var key = ClientKey.ForAddress("203.0.113.7", Salt);

        Assert.DoesNotContain("203.0.113.7", key);
        Assert.DoesNotContain(".", key);
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Key_is_deterministic_per_address_and_differs_between_addresses_and_salts()
    {
        Assert.Equal(ClientKey.ForAddress("203.0.113.7", Salt), ClientKey.ForAddress("203.0.113.7", Salt));
        Assert.NotEqual(ClientKey.ForAddress("203.0.113.7", Salt), ClientKey.ForAddress("203.0.113.8", Salt));
        Assert.NotEqual(ClientKey.ForAddress("203.0.113.7", Salt), ClientKey.ForAddress("203.0.113.7", "other-salt"));
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Ipv4_mapped_ipv6_addresses_hash_like_the_plain_ipv4_address()
    {
        Assert.Equal(ClientKey.ForAddress("203.0.113.7", Salt), ClientKey.ForAddress("::ffff:203.0.113.7", Salt));
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Forwarded_header_is_ignored_unless_the_proxy_is_trusted()
    {
        var untrusted = ClientKey.For(WithForwardedFor("203.0.113.9"), Options(trustProxy: false));

        Assert.Equal(ClientKey.ForAddress(null, Salt), untrusted);
        Assert.NotEqual(ClientKey.ForAddress("203.0.113.9", Salt), untrusted);
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Forwarded_header_is_used_when_the_proxy_is_trusted_and_the_rightmost_entry_wins()
    {
        var key = ClientKey.For(WithForwardedFor("198.51.100.1, 203.0.113.9"), Options(trustProxy: true));

        Assert.Equal(ClientKey.ForAddress("203.0.113.9", Salt), key);
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Garbage_forwarded_header_falls_back_to_the_connection_address()
    {
        var key = ClientKey.For(WithForwardedFor("definitely-not-an-address"), Options(trustProxy: true));

        Assert.Equal(ClientKey.ForAddress(null, Salt), key);
    }

    [Fact]
    [Trait("AC", "AC-ANA-3")]
    public void Default_salt_is_random_per_options_instance_and_never_empty()
    {
        var first = new ShortenerOptions().AnalyticsSalt;
        var second = new ShortenerOptions().AnalyticsSalt;

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
    }
}

public class PipelineTests
{
    private sealed class TestGate : IRequestGate
    {
        private readonly Func<HttpContext, Task<IResult?>> _behaviour;

        public TestGate(int order, Func<HttpContext, Task<IResult?>> behaviour)
        {
            Order = order;
            _behaviour = behaviour;
        }

        public int Order { get; }

        public Task<IResult?> BeforeAsync(HttpContext context) =>
            context.Request.Path.StartsWithSegments("/__gate") ? _behaviour(context) : Task.FromResult<IResult?>(null);
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task Unhandled_exceptions_become_a_generic_500_json_error()
    {
        using var app = new TestApp(s => s.AddSingleton<IRequestGate>(
            new TestGate(1, _ => throw new InvalidOperationException("secret connection string"))));

        var response = await app.Client.GetAsync("/__gate/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", await response.ErrorCodeAsync());
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("AC", "AC-SHORT-2")]
    public async Task ApiException_is_converted_to_json_with_its_status_code_and_headers()
    {
        using var app = new TestApp(s => s.AddSingleton<IRequestGate>(new TestGate(1, _ =>
            throw new ApiException(418, "teapot", "short and stout", new Dictionary<string, string> { ["X-Test"] = "yes" }))));

        var response = await app.Client.GetAsync("/__gate/x");

        Assert.Equal((HttpStatusCode)418, response.StatusCode);
        Assert.Equal("teapot", await response.ErrorCodeAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Test").Single());
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Request_gate_result_short_circuits_the_request()
    {
        using var app = new TestApp(s => s.AddSingleton<IRequestGate>(new TestGate(1, _ =>
            Task.FromResult<IResult?>(ApiResults.Error(429, "slow_down", "wait")))));

        var response = await app.Client.GetAsync("/__gate/anything");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal("slow_down", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-RL-1")]
    public async Task Request_gates_run_in_order_and_the_first_non_null_result_wins()
    {
        var calls = new List<string>();
        using var app = new TestApp(s =>
        {
            s.AddSingleton<IRequestGate>(new TestGate(20, _ =>
            {
                calls.Add("late");
                return Task.FromResult<IResult?>(ApiResults.Error(409, "late", "late"));
            }));
            s.AddSingleton<IRequestGate>(new TestGate(10, _ =>
            {
                calls.Add("early");
                return Task.FromResult<IResult?>(ApiResults.Error(409, "early", "early"));
            }));
        });

        var response = await app.Client.GetAsync("/__gate/order");

        Assert.Equal("early", await response.ErrorCodeAsync());
        Assert.Equal(new[] { "early" }, calls);
    }

    [Fact]
    [Trait("AC", "AC-REDIR-2")]
    public async Task Unknown_routes_return_a_json_404()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/no/such/route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    [Trait("AC", "AC-REDIR-1")]
    public async Task Responses_carry_security_headers_and_no_server_banner()
    {
        using var app = new TestApp();

        var response = await app.Client.GetAsync("/no/such/route");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public async Task Requests_are_counted_by_status_class()
    {
        using var app = new TestApp();

        await app.Client.GetAsync("/missing/one");
        await app.Client.GetAsync("/missing/two");
        await app.Client.GetAsync("/missing/three");

        var metrics = app.Get<IMetrics>().Snapshot();
        Assert.Equal(3, metrics["requests_total"]);
        Assert.Equal(3, metrics["requests_4xx"]);
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public async Task Server_errors_are_counted_in_the_5xx_class()
    {
        using var app = new TestApp(s => s.AddSingleton<IRequestGate>(
            new TestGate(1, _ => throw new InvalidOperationException("x"))));

        await app.Client.GetAsync("/__gate/boom");

        Assert.Equal(1, app.Get<IMetrics>().Snapshot()["requests_5xx"]);
    }

    [Fact]
    [Trait("AC", "AC-OBS-3")]
    public void Metrics_counters_are_thread_safe()
    {
        var metrics = new InMemoryMetrics();

        Parallel.For(0, 10_000, _ => metrics.Increment("hits"));
        metrics.Increment("hits", 5);

        Assert.Equal(10_005, metrics.Snapshot()["hits"]);
    }
}
