using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace UrlShortener.Tests.Core;

/// <summary>
/// Security hardening (URL validation + CSPRNG codes). The brownfield baseline ships weak v1 versions of
/// UrlValidator and CodeGenerator (DEF-1 / DEF-2); these tests fail against it and pass once hardened.
/// </summary>
public class HardeningTests
{
    private static string Code(string url) => UrlValidator.Validate(url).errorCode ?? "ok";

    // ---------------------------------------------------------------- AC-HARD-1: scheme allow-list

    [Theory]
    [Trait("AC", "AC-HARD-1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://localhost/c:/windows/win.ini")]
    [InlineData("ftp://example.com/file")]
    [InlineData("gopher://example.com/")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ws://example.com/socket")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("blob:https://example.com/uuid")]
    public void Non_http_schemes_are_rejected(string url)
    {
        var (ok, code) = UrlValidator.Validate(url);

        Assert.False(ok, $"{url} must be rejected");
        Assert.Equal("unsupported_scheme", code);
    }

    [Theory]
    [Trait("AC", "AC-HARD-1")]
    [InlineData("//example.com/path")]
    [InlineData("/relative/path")]
    [InlineData("http:example.com")]
    [InlineData("http:/example.com")]
    [InlineData("https:\\\\example.com")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\r\nSet-Cookie: a=b")]
    [InlineData("https://example.com/\u0000")]
    public void Malformed_or_control_character_urls_are_rejected(string url)
    {
        Assert.False(UrlValidator.Validate(url).ok, $"{url} must be rejected");
    }

    [Fact]
    [Trait("AC", "AC-HARD-1")]
    [Trait("AC", "AC-SHORT-2")]
    public void Urls_longer_than_2048_characters_are_rejected()
    {
        var url = "https://example.com/" + new string('a', 2049);

        Assert.Equal((false, "url_too_long"), UrlValidator.Validate(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-1")]
    [Trait("AC", "AC-SHORT-2")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    public async Task Api_rejects_dangerous_schemes_with_400(string url)
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_scheme", await response.ErrorCodeAsync());
    }

    // ---------------------------------------------------------------- AC-HARD-2: credentials and internal hosts

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://@example.com/")]
    [InlineData("https://example.com@evil.example.net/")]
    [InlineData("https://trusted.example.com%40evil.example.net/")]
    public void Urls_with_credentials_are_rejected(string url)
    {
        var (ok, code) = UrlValidator.Validate(url);

        // %40 stays inside the host and is rejected as an invalid host or url, the others as credentials.
        Assert.False(ok, $"{url} must be rejected");
        Assert.NotNull(code);
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://user:pass@example.com/", "credentials_not_allowed")]
    [InlineData("https://admin@example.com/", "credentials_not_allowed")]
    public void Credential_rejections_use_a_specific_error_code(string url, string expected)
    {
        Assert.Equal(expected, Code(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:8080/admin")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://localhost./")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://internal/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://intranet/")]
    public void Local_and_internal_hostnames_are_rejected(string url)
    {
        Assert.Equal("blocked_host", Code(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.255.255.254:9200/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://100.127.255.255/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://255.255.255.255/")]
    public void Loopback_private_link_local_metadata_and_cgnat_ipv4_are_rejected(string url)
    {
        Assert.Equal("blocked_host", Code(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://2130706433/")]            // decimal 127.0.0.1
    [InlineData("http://0x7f000001/")]            // hex 127.0.0.1
    [InlineData("http://0x7f.0x0.0x0.0x1/")]      // dotted hex
    [InlineData("http://0177.0.0.1/")]            // octal 127.0.0.1
    [InlineData("http://127.1/")]                 // short form
    [InlineData("http://3232235777/")]            // decimal 192.168.1.1
    [InlineData("http://0xA9FEA9FE/")]            // hex 169.254.169.254
    [InlineData("http://2852039166/latest/")]     // decimal 169.254.169.254
    [InlineData("http://0/")]                     // 0.0.0.0
    public void Obfuscated_ipv4_notations_are_normalised_and_rejected(string url)
    {
        Assert.Equal("blocked_host", Code(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[febf::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    [InlineData("http://[ff02::1]/")]
    public void Loopback_unique_local_link_local_and_mapped_ipv6_are_rejected(string url)
    {
        Assert.Equal("blocked_host", Code(url));
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("https://8.8.8.8/")]
    [InlineData("https://1.1.1.1/")]
    [InlineData("http://172.15.255.255/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://100.63.255.255/")]
    [InlineData("http://100.128.0.1/")]
    [InlineData("http://169.253.1.1/")]
    [InlineData("http://192.169.0.1/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    [InlineData("https://example.com/")]
    [InlineData("https://localhost.example.com/")]
    [InlineData("https://notlocalhost.com/")]
    [InlineData("https://my-internal-tool.example.com/")]
    public void Neighbouring_public_addresses_and_lookalike_hosts_are_still_accepted(string url)
    {
        var (ok, code) = UrlValidator.Validate(url);

        Assert.True(ok, $"{url} should be accepted but got {code}");
    }

    [Theory]
    [Trait("AC", "AC-HARD-2")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "blocked_host")]
    [InlineData("http://localhost:5000/", "blocked_host")]
    [InlineData("http://10.1.2.3/", "blocked_host")]
    [InlineData("http://user:pw@example.com/", "credentials_not_allowed")]
    public async Task Api_rejects_internal_hosts_and_credentials_with_400(string url, string expectedCode)
    {
        using var app = new TestApp();

        var response = await app.Client.ShortenAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await response.ErrorCodeAsync());
    }

    // ---------------------------------------------------------------- AC-HARD-3: CSPRNG codes

    [Fact]
    [Trait("AC", "AC-HARD-3")]
    public void Product_assembly_never_references_System_Random()
    {
        var types = ProductTypeReferences();

        Assert.DoesNotContain("System.Random", types);
    }

    [Fact]
    [Trait("AC", "AC-HARD-3")]
    public void Product_assembly_generates_codes_with_RandomNumberGenerator()
    {
        var types = ProductTypeReferences();

        Assert.Contains("System.Security.Cryptography.RandomNumberGenerator", types);
    }

    [Fact]
    [Trait("AC", "AC-HARD-3")]
    public void Codes_are_well_formed_and_spread_over_the_whole_alphabet()
    {
        var generator = new CodeGenerator();
        var counts = new Dictionary<char, int>();
        for (var i = 0; i < 20_000; i++)
        {
            foreach (var c in generator.Generate())
            {
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        Assert.Equal(62, counts.Count);
        var expected = 20_000 * 7 / 62.0;
        Assert.All(counts.Values, n => Assert.InRange(n, expected * 0.8, expected * 1.2));
    }

    private static HashSet<string> ProductTypeReferences()
    {
        var path = typeof(CodeGenerator).Assembly.Location;
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new HashSet<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            names.Add(reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name));
        }

        return names;
    }
}
