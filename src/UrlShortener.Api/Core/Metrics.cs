using System.Collections.Concurrent;

namespace UrlShortener.Api.Core;

public interface IMetrics
{
    void Increment(string name, long by = 1);

    /// <summary>Point-in-time copy of all counters, ordered by name.</summary>
    IReadOnlyDictionary<string, long> Snapshot();
}

public sealed class InMemoryMetrics : IMetrics
{
    private sealed class Counter
    {
        public long Value;
    }

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    public void Increment(string name, long by = 1)
    {
        var counter = _counters.GetOrAdd(name, static _ => new Counter());
        Interlocked.Add(ref counter.Value, by);
    }

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in _counters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            result[pair.Key] = Interlocked.Read(ref pair.Value.Value);
        }

        return result;
    }
}
