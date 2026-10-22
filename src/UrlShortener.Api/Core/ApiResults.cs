using System.Text;
using System.Text.Json;

namespace UrlShortener.Api.Core;

/// <summary>Shared JSON serializer settings (camelCase, nulls kept) so every slice emits identical shapes.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
}

/// <summary>Result factories usable from endpoints and from request gates.</summary>
public static class ApiResults
{
    public static IResult Error(int status, string code, string message, IReadOnlyDictionary<string, string>? headers = null) =>
        new RawJsonResult(status, ApiJson.Serialize(new { error = new { code, message } }), headers);

    public static IResult Json(int status, object body, IReadOnlyDictionary<string, string>? headers = null) =>
        new RawJsonResult(status, ApiJson.Serialize(body), headers);

    /// <summary>Writes an already-serialized JSON document (used for idempotent replays).</summary>
    public static IResult RawJson(int status, string json, IReadOnlyDictionary<string, string>? headers = null) =>
        new RawJsonResult(status, json, headers);

    private sealed class RawJsonResult : IResult
    {
        private readonly int _status;
        private readonly string _json;
        private readonly IReadOnlyDictionary<string, string>? _headers;

        public RawJsonResult(int status, string json, IReadOnlyDictionary<string, string>? headers)
        {
            _status = status;
            _json = json;
            _headers = headers;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = _status;
            if (_headers is not null)
            {
                foreach (var (name, value) in _headers)
                {
                    response.Headers[name] = value;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(_json);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = bytes.Length;
            return response.Body.WriteAsync(bytes, 0, bytes.Length, httpContext.RequestAborted);
        }
    }
}
