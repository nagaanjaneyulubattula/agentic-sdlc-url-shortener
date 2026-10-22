namespace UrlShortener.Api.Core;

/// <summary>Runs every registered <see cref="IRequestGate"/> (ordered) before routing; a non-null result ends the request.</summary>
public sealed class RequestGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRequestGate[] _gates;

    public RequestGateMiddleware(RequestDelegate next, IEnumerable<IRequestGate> gates)
    {
        _next = next;
        _gates = gates.OrderBy(g => g.Order).ToArray();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        foreach (var gate in _gates)
        {
            var result = await gate.BeforeAsync(context);
            if (result is not null)
            {
                await result.ExecuteAsync(context);
                return;
            }
        }

        await _next(context);
    }
}
