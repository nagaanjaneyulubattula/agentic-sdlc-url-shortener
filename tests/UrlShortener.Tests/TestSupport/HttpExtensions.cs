using System.Net;
using System.Text;
using System.Text.Json;
using UrlShortener.Api.Core;

namespace UrlShortener.Tests.TestSupport;

public static class HttpExtensions
{
    public const string ExampleUrl = "https://example.com/some/path?x=1";

    public static Task<HttpResponseMessage> PostRawAsync(this HttpClient client, string json,
        IDictionary<string, string>? headers = null, string path = "/api/links")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostLinkAsync(this HttpClient client, object body,
        IDictionary<string, string>? headers = null) =>
        client.PostRawAsync(JsonSerializer.Serialize(body), headers);

    public static Task<HttpResponseMessage> ShortenAsync(this HttpClient client, string url = ExampleUrl,
        IDictionary<string, string>? headers = null) =>
        client.PostLinkAsync(new { url }, headers);

    public static Task<HttpResponseMessage> GetWithAsync(this HttpClient client, string path,
        IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return client.SendAsync(request);
    }

    /// <summary>Header set that makes a request appear to come from the given client address (needs Shortener:TrustProxy=true).</summary>
    public static Dictionary<string, string> From(string clientAddress) =>
        new() { [ClientKey.ForwardedForHeader] = clientAddress };

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var json = await response.JsonAsync();
        return json.GetProperty("error").GetProperty("code").GetString()!;
    }

    /// <summary>Creates a link and returns its code (asserts 201).</summary>
    public static async Task<string> CreateCodeAsync(this HttpClient client, string url = ExampleUrl,
        IDictionary<string, string>? headers = null)
    {
        var response = await client.ShortenAsync(url, headers);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("code").GetString()!;
    }
}
