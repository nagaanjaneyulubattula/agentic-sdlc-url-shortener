using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Shorten;

public sealed class ShortenFeature : IFeature
{
    public string Id => "shorten";

    public int Order => 10;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        // Cast to Delegate: otherwise the method group binds to RequestDelegate and the returned IResult is discarded.
        endpoints.MapPost(Routes.Links, (Delegate)ShortenEndpoint.HandleAsync);
}
