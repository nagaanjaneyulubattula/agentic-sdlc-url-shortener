using System.Net;
using System.Net.Sockets;

namespace UrlShortener.Api.Core;

/// <summary>
/// Validates destination URLs. Allow-list of schemes, no credentials, and no internal / private / metadata hosts
/// (open-redirect and SSRF hardening). Hostnames written as decimal / hex / octal IPs are normalised by <see cref="Uri"/>
/// before the address checks run, so "http://2130706433/" is treated as 127.0.0.1.
/// Note: DNS names that merely *resolve* to private addresses cannot be detected without resolving them.
/// </summary>
public static class UrlValidator
{
    public const int MaxLength = 2048;

    private static readonly string[] BlockedSuffixes =
    {
        ".localhost", ".local", ".internal", ".localdomain", ".home.arpa",
    };

    public static (bool ok, string? errorCode) Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "invalid_url");
        }

        if (url.Length > MaxLength)
        {
            return (false, "url_too_long");
        }

        foreach (var ch in url)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return (false, "invalid_url");
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (false, "invalid_url");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return (false, "unsupported_scheme");
        }

        // Require the canonical "scheme://" form (rejects http:example.com, http:/example.com, http:\\host ...).
        if (!url.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "invalid_url");
        }

        if (HasUserInfo(url, uri))
        {
            return (false, "credentials_not_allowed");
        }

        return IsBlockedHost(uri) ? (false, "blocked_host") : (true, null);
    }

    private static bool HasUserInfo(string url, Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOfAny(new[] { '/', '?', '#', '\\' }, start);
        var authority = end < 0 ? url[start..] : url[start..end];
        return authority.Contains('@');
    }

    private static bool IsBlockedHost(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        var zone = host.IndexOf('%');
        if (zone >= 0)
        {
            host = host[..zone];
        }

        if (host.Length == 0)
        {
            return true;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
            IPAddress.TryParse(host, out _))
        {
            return !IPAddress.TryParse(host, out var address) || IsBlockedAddress(address);
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || host == "local" || host == "internal")
        {
            return true;
        }

        foreach (var suffix in BlockedSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Single-label names ("http://metadata/", "http://intranet/") resolve through search domains.
        return !host.Contains('.');
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsBlockedV6(address);
        }

        return IsBlockedV4(address.GetAddressBytes());
    }

    private static bool IsBlockedV4(ReadOnlySpan<byte> b)
    {
        var first = b[0];
        var second = b[1];
        return first == 0                                          // 0.0.0.0/8 "this network"
            || first == 10                                         // 10/8
            || first == 127                                        // loopback
            || (first == 100 && second >= 64 && second <= 127)     // CGNAT 100.64/10
            || (first == 169 && second == 254)                     // link-local + cloud metadata
            || (first == 172 && second >= 16 && second <= 31)      // 172.16/12
            || (first == 192 && second == 168)                     // 192.168/16
            || (first == 192 && second == 0 && b[2] == 0)          // 192.0.0.0/24 IETF protocol assignments
            || (first == 198 && (second == 18 || second == 19))    // benchmarking
            || first >= 224;                                       // multicast, reserved, broadcast
    }

    private static bool IsBlockedV6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedV4(address.MapToIPv4().GetAddressBytes());
        }

        var b = address.GetAddressBytes();

        if ((b[0] & 0xFE) == 0xFC)                          // fc00::/7 unique local
        {
            return true;
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) is 0x80 or 0xC0)  // fe80::/10 link-local, fec0::/10 site-local
        {
            return true;
        }

        if (b[0] == 0xFF)                                   // multicast
        {
            return true;
        }

        if (AllZero(b[..12]))                               // ::, ::1 and IPv4-compatible ::a.b.c.d
        {
            return IsBlockedV4(b.AsSpan(12, 4));
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && AllZero(b[4..12]))  // NAT64
        {
            return IsBlockedV4(b.AsSpan(12, 4));
        }

        if (b[0] == 0x20 && b[1] == 0x02)                   // 6to4 embeds an IPv4 address
        {
            return IsBlockedV4(b.AsSpan(2, 4));
        }

        return false;
    }

    private static bool AllZero(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
