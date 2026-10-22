using UrlShortener.Api.Core;

namespace UrlShortener.Tests.TestSupport;

/// <summary>Link store decorator for fault injection: forced collisions, failing reads, failing readiness.</summary>
public sealed class FaultyLinkStore : ILinkStore
{
    private readonly InMemoryLinkStore _inner = new();
    private int _createAttempts;

    /// <summary>The first N TryCreate calls report a collision (return false).</summary>
    public int CollideFirst { get; set; }

    public bool ThrowOnGet { get; set; }

    public bool ThrowOnPing { get; set; }

    public int CreateAttempts => Volatile.Read(ref _createAttempts);

    public bool TryCreate(Link link)
    {
        var attempt = Interlocked.Increment(ref _createAttempts);
        return attempt > CollideFirst && _inner.TryCreate(link);
    }

    public Link? Get(string code) =>
        ThrowOnGet ? throw new InvalidOperationException("store unavailable (injected)") : _inner.Get(code);

    public bool Delete(string code) => _inner.Delete(code);

    public void Ping()
    {
        if (ThrowOnPing)
        {
            throw new InvalidOperationException("store unreachable (injected)");
        }
    }
}
