namespace UrlShortener.Api.Core;

/// <summary>
/// A vertical feature slice. Slices are self-contained folders under Features/ and only ever talk to each
/// other through the core hook interfaces (IRequestGate, IShortenHook, IRedirectHook).
/// </summary>
public interface IFeature
{
    string Id { get; }

    /// <summary>Registration order (lower first). Also used as the default hook/gate order of the slice.</summary>
    int Order { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
