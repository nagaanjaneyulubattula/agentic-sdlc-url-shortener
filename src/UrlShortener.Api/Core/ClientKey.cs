using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Api.Core;

/// <summary>
/// The ONLY place in the product that touches the connection's remote address or the forwarded-for header.
/// Everything else (rate limiting, analytics, idempotency scoping) uses the keyed hash returned here, so raw
/// client addresses are never stored, logged or returned.
/// </summary>
public static class ClientKey
{
    public const string ForwardedForHeader = "X-Forwarded-For";

    public static string For(HttpContext context, ShortenerOptions options)
    {
        string? address = context.Connection.RemoteIpAddress?.ToString();

        if (options.TrustProxy &&
            context.Request.Headers.TryGetValue(ForwardedForHeader, out var forwarded) &&
            forwarded.Count > 0)
        {
            // The right-most entry is the one appended by our (single) trusted proxy; earlier entries are client-controlled.
            var entries = string.Join(',', forwarded.ToArray()).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length > 0 && IPAddress.TryParse(entries[^1], out var parsed))
            {
                address = parsed.ToString();
            }
        }

        return ForAddress(address, options.AnalyticsSalt);
    }

    /// <summary>First 16 hex chars of HMAC-SHA256(salt, address).</summary>
    public static string ForAddress(string? address, string salt)
    {
        var key = Encoding.UTF8.GetBytes(salt);
        var data = Encoding.UTF8.GetBytes(Normalise(address));
        return Convert.ToHexString(HMACSHA256.HashData(key, data))[..16].ToLowerInvariant();
    }

    private static string Normalise(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "unknown";
        }

        if (IPAddress.TryParse(address, out var parsed))
        {
            return (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        }

        return address.Trim();
    }
}
