namespace UrlShortener.Api.Core;

/// <summary>
/// Thrown anywhere in the pipeline (endpoints, hooks, gates) to produce a JSON error
/// <c>{"error":{"code":"...","message":"..."}}</c> with the given HTTP status.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message, IReadOnlyDictionary<string, string>? headers = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Headers = headers;
    }

    public int Status { get; }

    public string Code { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }
}
