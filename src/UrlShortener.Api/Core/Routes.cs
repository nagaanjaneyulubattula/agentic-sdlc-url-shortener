namespace UrlShortener.Api.Core;

public static class Routes
{
    public const string Links = "/api/links";

    /// <summary>True for POST /api/links (trailing slash and case tolerant, like endpoint routing).</summary>
    public static bool IsCreateLink(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) &&
        string.Equals(context.Request.Path.Value?.TrimEnd('/'), Links, StringComparison.OrdinalIgnoreCase);
}

public static class ShortUrl
{
    /// <summary>Public short URL: configured BaseUrl, otherwise the request's scheme and host.</summary>
    public static string Build(HttpContext context, ShortenerOptions options, string code)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : options.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/{code}";
    }
}

/// <summary>Codes that must never be issued: they would be shadowed by (or shadow) system routes.</summary>
public static class ReservedCodes
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "healthz", "readyz", "metrics", "admin",
    };

    public static bool IsReserved(string code) => Words.Contains(code);
}
