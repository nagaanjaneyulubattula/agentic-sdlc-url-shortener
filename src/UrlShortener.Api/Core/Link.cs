using System.Collections.Concurrent;

namespace UrlShortener.Api.Core;

/// <summary>A stored short link. Codes are case-sensitive.</summary>
public sealed record Link(string Code, string Url, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

public interface ILinkStore
{
    /// <summary>Atomically inserts the link. Returns false (and changes nothing) if the code already exists.</summary>
    bool TryCreate(Link link);

    Link? Get(string code);

    /// <summary>Removes the link. Returns false when the code was unknown.</summary>
    bool Delete(string code);

    /// <summary>Readiness probe. Throws when the backing store is unreachable.</summary>
    void Ping();
}

public sealed class InMemoryLinkStore : ILinkStore
{
    private readonly ConcurrentDictionary<string, Link> _links = new(StringComparer.Ordinal);

    public bool TryCreate(Link link) => _links.TryAdd(link.Code, link);

    public Link? Get(string code) => _links.TryGetValue(code, out var link) ? link : null;

    public bool Delete(string code) => _links.TryRemove(code, out _);

    public void Ping()
    {
    }
}
