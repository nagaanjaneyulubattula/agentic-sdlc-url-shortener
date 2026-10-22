namespace UrlShortener.Api.Core;

/// <summary>Adds conservative security headers to every response (never overriding a header an endpoint already set).</summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
            headers.TryAdd("Cache-Control", "no-store");
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
