namespace UrlShortener.Api.Features.Idempotency;

public enum IdempotencyOutcome
{
    /// <summary>First time this key is seen (or the previous entry expired): the caller now owns the reservation.</summary>
    Started,

    /// <summary>Same key, same body, original response available.</summary>
    Replay,

    /// <summary>Same key, different body.</summary>
    Mismatch,

    /// <summary>Same key, same body, but the original request has not finished yet.</summary>
    InProgress,
}

public sealed record StoredResponse(int Status, string Body);

public sealed class IdempotencyReservation
{
    internal IdempotencyReservation(string key, long sequence)
    {
        Key = key;
        Sequence = sequence;
    }

    internal string Key { get; }

    internal long Sequence { get; }
}

/// <summary>Bounded (10,000 entries by default) in-memory key store whose entries expire after 24 hours.</summary>
public sealed class IdempotencyStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    public const int DefaultMaxEntries = 10_000;

    private sealed class Entry
    {
        public required string Key { get; init; }
        public required long Sequence { get; init; }
        public required string Fingerprint { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public StoredResponse? Response { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _order = new(); // insertion order == expiry order (constant ttl)
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private long _sequence;

    public IdempotencyStore()
        : this(DefaultTtl, DefaultMaxEntries)
    {
    }

    public IdempotencyStore(TimeSpan ttl, int maxEntries)
    {
        _ttl = ttl;
        _maxEntries = Math.Max(1, maxEntries);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public IdempotencyOutcome Begin(string key, string fingerprint, DateTimeOffset now,
        out IdempotencyReservation? reservation, out StoredResponse? stored)
    {
        reservation = null;
        stored = null;
        lock (_gate)
        {
            Purge(now);
            if (_order.Count > _maxEntries * 2)
            {
                Compact();
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return IdempotencyOutcome.Mismatch;
                }

                if (existing.Response is null)
                {
                    return IdempotencyOutcome.InProgress;
                }

                stored = existing.Response;
                return IdempotencyOutcome.Replay;
            }

            while (_entries.Count >= _maxEntries && _order.TryDequeue(out var oldest))
            {
                RemoveIfCurrent(oldest);
            }

            var entry = new Entry { Key = key, Sequence = ++_sequence, Fingerprint = fingerprint, CreatedAt = now };
            _entries[key] = entry;
            _order.Enqueue(entry);
            reservation = new IdempotencyReservation(key, entry.Sequence);
            return IdempotencyOutcome.Started;
        }
    }

    public void Complete(IdempotencyReservation reservation, int status, string body)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reservation.Key, out var entry) && entry.Sequence == reservation.Sequence)
            {
                entry.Response = new StoredResponse(status, body);
            }
        }
    }

    /// <summary>Releases a reservation that never received a response (no-op once completed).</summary>
    public void Abandon(IdempotencyReservation reservation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reservation.Key, out var entry) &&
                entry.Sequence == reservation.Sequence &&
                entry.Response is null)
            {
                _entries.Remove(reservation.Key);
            }
        }
    }

    private void Purge(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && (now - oldest.CreatedAt >= _ttl || !IsCurrent(oldest)))
        {
            _order.Dequeue();
            RemoveIfCurrent(oldest);
        }
    }

    // Abandoned reservations leave stale queue slots behind; rebuild the queue so it can never outgrow the key table.
    private void Compact()
    {
        var live = _order.Where(IsCurrent).ToList();
        _order.Clear();
        foreach (var entry in live)
        {
            _order.Enqueue(entry);
        }
    }

    private bool IsCurrent(Entry entry) =>
        _entries.TryGetValue(entry.Key, out var current) && current.Sequence == entry.Sequence;

    private void RemoveIfCurrent(Entry entry)
    {
        if (IsCurrent(entry))
        {
            _entries.Remove(entry.Key);
        }
    }
}
