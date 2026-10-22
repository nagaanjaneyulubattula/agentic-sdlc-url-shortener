namespace UrlShortener.Api.Features.RateLimit;

/// <summary>Thread-safe token bucket per key with an LRU-bounded key table (memory cannot grow without bound).</summary>
public sealed class TokenBucketLimiter
{
    private sealed class Bucket
    {
        public required string Key { get; init; }
        public double Tokens;
        public DateTimeOffset LastRefill;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Bucket>> _buckets = new(StringComparer.Ordinal);
    private readonly LinkedList<Bucket> _lru = new(); // first = most recently used
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly int _maxKeys;

    public TokenBucketLimiter(double capacity, double refillPerSecond, int maxKeys)
    {
        _capacity = Math.Max(1, capacity);
        _refillPerSecond = Math.Max(0, refillPerSecond);
        _maxKeys = Math.Max(1, maxKeys);
    }

    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Takes one token. When none is available returns false and how long until one is.</summary>
    public bool TryAcquire(string key, DateTimeOffset now, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            if (_buckets.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                var elapsed = Math.Max(0, (now - node.Value.LastRefill).TotalSeconds);
                node.Value.Tokens = Math.Min(_capacity, node.Value.Tokens + (elapsed * _refillPerSecond));
                node.Value.LastRefill = now;
            }
            else
            {
                node = _lru.AddFirst(new Bucket { Key = key, Tokens = _capacity, LastRefill = now });
                _buckets[key] = node;
                while (_buckets.Count > _maxKeys)
                {
                    var oldest = _lru.Last!;
                    _lru.RemoveLast();
                    _buckets.Remove(oldest.Value.Key);
                }
            }

            var bucket = node.Value;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = _refillPerSecond > 0
                ? TimeSpan.FromSeconds((1 - bucket.Tokens) / _refillPerSecond)
                : TimeSpan.FromHours(1);
            return false;
        }
    }
}
