using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Manage;

public sealed class ManageFeature : IFeature
{
    public string Id => "manage";

    public int Order => 30;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Routes.Links + "/{code}", (HttpContext context, string code) =>
        {
            var link = context.RequestServices.GetRequiredService<ILinkStore>().Get(code) ?? throw NotFound();
            var options = context.RequestServices.GetRequiredService<ShortenerOptions>();
            return ApiResults.Json(StatusCodes.Status200OK, new
            {
                code = link.Code,
                shortUrl = ShortUrl.Build(context, options, link.Code),
                url = link.Url,
                createdAt = link.CreatedAt,
                expiresAt = link.ExpiresAt,
            });
        });

        endpoints.MapDelete(Routes.Links + "/{code}", (HttpContext context, string code) =>
        {
            if (!context.RequestServices.GetRequiredService<ILinkStore>().Delete(code))
            {
                throw NotFound();
            }

            return Results.NoContent();
        });
    }

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found", "Unknown short code.");
}
