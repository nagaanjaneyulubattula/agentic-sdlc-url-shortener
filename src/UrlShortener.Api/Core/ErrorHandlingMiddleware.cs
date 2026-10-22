using Microsoft.AspNetCore.Http.Features;

namespace UrlShortener.Api.Core;

/// <summary>
/// Outermost middleware: converts <see cref="ApiException"/> to JSON errors, hides unexpected failures behind a generic
/// 500 (details go to the log only), gives bare 404/405 responses a JSON body and counts requests by status class.
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMetrics _metrics;
    private readonly ShortenerOptions _options;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, IMetrics metrics, ShortenerOptions options, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            sizeLimit.MaxRequestBodySize = _options.MaxBodyBytes;
        }

        try
        {
            await _next(context);

            if (!context.Response.HasStarted &&
                context.Response.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed &&
                context.Response.ContentLength is null &&
                string.IsNullOrEmpty(context.Response.ContentType))
            {
                var notFound = context.Response.StatusCode == StatusCodes.Status404NotFound;
                await WriteAsync(context, notFound ? StatusCodes.Status404NotFound : StatusCodes.Status405MethodNotAllowed,
                    notFound ? "not_found" : "method_not_allowed",
                    notFound ? "Resource not found." : "Method not allowed.", null, keepHeaders: true);
            }
        }
        catch (ApiException ex)
        {
            await WriteAsync(context, ex.Status, ex.Code, ex.Message, ex.Headers);
        }
        catch (BadHttpRequestException ex)
        {
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Request body is too large.", null);
            }
            else
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest, "bad_request", "The request could not be processed.", null);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.", null);
        }
        finally
        {
            Count(context.Response.StatusCode);
        }
    }

    private void Count(int status)
    {
        _metrics.Increment("requests_total");
        var statusClass = status / 100;
        if (statusClass is >= 1 and <= 5)
        {
            _metrics.Increment($"requests_{statusClass}xx");
        }
    }

    private async Task WriteAsync(HttpContext context, int status, string code, string message,
        IReadOnlyDictionary<string, string>? headers, bool keepHeaders = false)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogWarning("Response already started; cannot write error {Code}", code);
            context.Abort();
            return;
        }

        if (!keepHeaders)
        {
            context.Response.Clear();
        }

        await ApiResults.Error(status, code, message, headers).ExecuteAsync(context);
    }
}
