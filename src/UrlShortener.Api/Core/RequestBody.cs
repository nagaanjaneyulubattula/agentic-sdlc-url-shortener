using System.Text.Json;

namespace UrlShortener.Api.Core;

/// <summary>Reads request bodies with a hard size cap and caches the bytes so gates and endpoints can both read them.</summary>
public static class RequestBody
{
    private const string ItemKey = "UrlShortener.RawBody";

    public static async Task<byte[]> ReadAsync(HttpContext context, int maxBytes)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is byte[] bytes)
        {
            return bytes;
        }

        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            throw TooLarge(maxBytes);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk.AsMemory(), context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw TooLarge(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        bytes = buffer.ToArray();
        context.Items[ItemKey] = bytes;
        return bytes;
    }

    /// <summary>Parses the body as a JSON object. Malformed JSON -> 400 invalid_json; non-object -> 400 invalid_request.</summary>
    public static JsonDocument ParseObject(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_json", "Request body is not valid JSON.");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "Request body must be a JSON object.");
        }

        return document;
    }

    private static ApiException TooLarge(int maxBytes) =>
        new(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"Request body must not exceed {maxBytes} bytes.");
}
