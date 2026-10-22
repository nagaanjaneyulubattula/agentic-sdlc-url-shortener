using System.Security.Cryptography;

namespace UrlShortener.Api.Core;

/// <summary>Bound from configuration section "Shortener".</summary>
public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Public base URL used to build shortUrl. When empty the request scheme + host is used.</summary>
    public string? BaseUrl { get; set; }

    public int MaxBodyBytes { get; set; } = 16384;

    public int MaxUrlLength { get; set; } = 2048;

    public int CodeLength { get; set; } = 7;

    /// <summary>Key for hashing client addresses. Random per process unless configured.</summary>
    public string AnalyticsSalt { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Honour the forwarded-for header (only enable behind a trusted reverse proxy).</summary>
    public bool TrustProxy { get; set; }
}
