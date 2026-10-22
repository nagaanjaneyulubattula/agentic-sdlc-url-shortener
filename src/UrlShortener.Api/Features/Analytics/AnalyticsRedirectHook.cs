using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Analytics;

/// <summary>Records a click after every successful redirect. Core isolates failures from the redirect response.</summary>
public sealed class AnalyticsRedirectHook : IRedirectHook
{
    private readonly IClickStore _store;
    private readonly TimeProvider _time;
    private readonly ShortenerOptions _options;

    public AnalyticsRedirectHook(IClickStore store, TimeProvider time, ShortenerOptions options)
    {
        _store = store;
        _time = time;
        _options = options;
    }

    public int Order => 60;

    public Task AfterAsync(RedirectContext context)
    {
        var referrerHost = ParseReferrerHost(context.Http.Request.Headers.Referer.ToString());
        var clientKey = ClientKey.For(context.Http, _options);
        _store.Record(context.Link.Code, new ClickEvent(_time.GetUtcNow(), referrerHost, clientKey));
        return Task.CompletedTask;
    }

    /// <summary>Lower-cased host of an http(s) Referer URL; null when absent or not a valid absolute http(s) URL.</summary>
    public static string? ParseReferrerHost(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) ||
            !Uri.TryCreate(referer, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.IdnHost.ToLowerInvariant();
    }
}
