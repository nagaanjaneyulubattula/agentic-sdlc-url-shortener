using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using UrlShortener.Api.Core;

namespace UrlShortener.Tests.TestSupport;

/// <summary>
/// In-memory test host over the real composition root (Program + FeatureRegistry.All, i.e. whatever slices are installed).
/// Uses a FakeTimeProvider, a fresh in-memory store and generous rate-limit settings so unrelated slices never interfere.
/// </summary>
public sealed class TestApp : IDisposable
{
    public static readonly DateTimeOffset StartTime = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;

    public TestApp(Action<IServiceCollection>? configure = null, IDictionary<string, string?>? config = null)
    {
        Time = new FakeTimeProvider(StartTime);

        var settings = new Dictionary<string, string?>
        {
            ["RateLimit:Capacity"] = "100000",
            ["RateLimit:RefillPerSecond"] = "100000",
            ["Shortener:AnalyticsSalt"] = "test-salt-0123456789",
        };
        if (config is not null)
        {
            foreach (var (key, value) in config)
            {
                settings[key] = value;
            }
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                services.RemoveAll<ILinkStore>();
                services.AddSingleton<ILinkStore, InMemoryLinkStore>();
                configure?.Invoke(services);
            });
        });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public FakeTimeProvider Time { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Creates a second client (same server) - handy for simulating several callers.</summary>
    public HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }
}
