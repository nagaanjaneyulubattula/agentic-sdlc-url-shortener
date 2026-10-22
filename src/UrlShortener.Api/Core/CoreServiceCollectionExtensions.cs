using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers core services. Everything uses TryAdd so tests/hosts can pre-register replacements.</summary>
    public static IServiceCollection AddUrlShortenerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ShortenerOptions>(configuration.GetSection(ShortenerOptions.SectionName));
        services.TryAddSingleton<ShortenerOptions>(sp => sp.GetRequiredService<IOptions<ShortenerOptions>>().Value);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILinkStore, InMemoryLinkStore>();
        services.TryAddSingleton<IMetrics, InMemoryMetrics>();
        services.TryAddSingleton<CodeGenerator>();
        return services;
    }
}
