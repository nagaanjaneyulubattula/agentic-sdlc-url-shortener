using System.Security.Cryptography;
using UrlShortener.Api.Core;

namespace UrlShortener.Api.Features.Idempotency;

public sealed class IdempotencyFeature : IFeature
{
    public string Id => "idempotency";

    public int Order => 7;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IdempotencyStore>();
        services.AddSingleton<IRequestGate, IdempotencyGate>();
        services.AddSingleton<IShortenHook, IdempotencyShortenHook>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>
/// Before the endpoint: replays a stored response for a repeated Idempotency-Key, rejects key reuse with a different body
/// (422) and reserves the key for the in-flight request. Keys are scoped per client (hashed) so one client can never
/// replay another client's response.
/// </summary>
public sealed class IdempotencyGate : IRequestGate
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxKeyLength = 255;
    internal const string ReservationItem = "UrlShortener.Idempotency.Reservation";

    private readonly IdempotencyStore _store;
    private readonly TimeProvider _time;
    private readonly ShortenerOptions _options;

    public IdempotencyGate(IdempotencyStore store, TimeProvider time, ShortenerOptions options)
    {
        _store = store;
        _time = time;
        _options = options;
    }

    public int Order => 7;

    public async Task<IResult?> BeforeAsync(HttpContext context)
    {
        if (!Routes.IsCreateLink(context) || !context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        var key = values.Count == 1 ? values[0]!.Trim() : string.Empty;
        if (key.Length == 0 || key.Length > MaxKeyLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_idempotency_key",
                $"{HeaderName} must be between 1 and {MaxKeyLength} characters.");
        }

        var body = await RequestBody.ReadAsync(context, _options.MaxBodyBytes);
        var fingerprint = Convert.ToHexString(SHA256.HashData(body));
        var scopedKey = ClientKey.For(context, _options) + ":" + key;

        var outcome = _store.Begin(scopedKey, fingerprint, _time.GetUtcNow(), out var reservation, out var stored);
        switch (outcome)
        {
            case IdempotencyOutcome.Replay:
                return ApiResults.RawJson(stored!.Status, stored.Body, new Dictionary<string, string> { ["Idempotent-Replay"] = "true" });

            case IdempotencyOutcome.Mismatch:
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "idempotency_key_reuse",
                    $"{HeaderName} was already used with a different request body.");

            case IdempotencyOutcome.InProgress:
                throw new ApiException(StatusCodes.Status409Conflict, "idempotency_in_progress",
                    "A request with this key is still being processed.",
                    new Dictionary<string, string> { ["Retry-After"] = "1" });

            default:
                context.Items[ReservationItem] = reservation;
                // If the request does not complete with a stored response (validation error...), free the key for a retry.
                context.Response.OnCompleted(() =>
                {
                    _store.Abandon(reservation!);
                    return Task.CompletedTask;
                });
                return null;
        }
    }
}

/// <summary>After a successful create: stores the response so later retries with the same key replay it.</summary>
public sealed class IdempotencyShortenHook : IShortenHook
{
    private readonly IdempotencyStore _store;

    public IdempotencyShortenHook(IdempotencyStore store) => _store = store;

    public int Order => 7;

    public Task AfterShortenAsync(ShortenContext context, Link created, int status, object responseBody)
    {
        if (context.Http.Items.TryGetValue(IdempotencyGate.ReservationItem, out var item) && item is IdempotencyReservation reservation)
        {
            _store.Complete(reservation, status, ApiJson.Serialize(responseBody));
        }

        return Task.CompletedTask;
    }
}
