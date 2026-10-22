using System.Text.Json;
using System.Text.RegularExpressions;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Alias;

public sealed class AliasFeature : IFeature
{
    public string Id => "alias";

    public int Order => 40;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IShortenHook, AliasShortenHook>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>Lets callers pick their own code via the optional "alias" body field.</summary>
public sealed partial class AliasShortenHook : IShortenHook
{
    public int Order => 40;

    // \z (not $) so "abc\n" is rejected. Case-sensitive: "Promo" and "promo" are different aliases.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{3,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    public Task<string?> ResolveCodeAsync(ShortenContext context)
    {
        if (!context.Body.TryGetProperty("alias", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return Task.FromResult<string?>(null);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_alias", "'alias' must be a string.");
        }

        var alias = element.GetString() ?? string.Empty;
        if (!AliasPattern().IsMatch(alias))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_alias", "'alias' must match [A-Za-z0-9_-]{3,32}.");
        }

        // Reserved words are compared case-insensitively because route matching for system paths is case-insensitive.
        if (ReservedCodes.IsReserved(alias))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "alias_reserved", "That alias is reserved.");
        }

        context.ConflictErrorCode = "alias_taken";
        return Task.FromResult<string?>(alias);
    }
}
