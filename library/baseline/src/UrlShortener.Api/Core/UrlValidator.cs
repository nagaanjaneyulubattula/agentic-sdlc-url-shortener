// v1 baseline (known issues: DEF-1 / DEF-2)
// DEF-1 (CWE-601 / CWE-918): this validator only checks that the string parses as an absolute URI. It accepts
// javascript:, data:, file: and other schemes, URLs carrying credentials, and localhost / private / link-local /
// cloud-metadata hosts (e.g. http://169.254.169.254/).
namespace UrlShortener.Api.Core;

public static class UrlValidator
{
    public const int MaxLength = 2048;

    public static (bool ok, string? errorCode) Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "invalid_url");
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _) ? (true, null) : (false, "invalid_url");
    }
}
