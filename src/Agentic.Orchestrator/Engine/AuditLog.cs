using System.Text.Json.Nodes;

namespace Agentic.Orchestrator;

public sealed record AuditEntry(long Seq, DateTimeOffset Ts, string Type, string? NodeId, string Actor, string Message, JsonNode Data, string PrevHash, string Hash);

/// <summary>
/// Append-only, hash-chained audit log (each entry commits to the previous one) → tamper-evident trail of every
/// state transition, gate verdict, policy violation, approval and re-plan. Optionally mirrored to a JSONL file.
/// </summary>
public sealed class AuditLog
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = new();
    private readonly string? _path;
    private readonly TimeProvider _time;

    public AuditLog(TimeProvider time, string? jsonlPath = null)
    {
        _time = time;
        _path = jsonlPath;
        if (_path is not null) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, ""); }
    }

    public IReadOnlyList<AuditEntry> Entries { get { lock (_lock) return _entries.ToList(); } }
    public string HeadHash { get { lock (_lock) return _entries.Count == 0 ? Genesis : _entries[^1].Hash; } }

    public AuditEntry Append(string type, string? nodeId, string actor, string message, object? data = null)
    {
        lock (_lock)
        {
            var seq = _entries.Count + 1;
            var prev = _entries.Count == 0 ? Genesis : _entries[^1].Hash;
            var ts = _time.GetUtcNow();
            var node = data is null ? new JsonObject() : Json.ToNode(data);
            var entry = new AuditEntry(seq, ts, type, nodeId, actor, message, node, prev, ComputeHash(prev, seq, ts, type, nodeId, actor, message, node));
            _entries.Add(entry);
            if (_path is not null) File.AppendAllText(_path, Json.Serialize(entry) + Environment.NewLine);
            return entry;
        }
    }

    /// <summary>Recomputes the chain; returns the first sequence number that does not verify (null when intact).</summary>
    public (bool Valid, long? BrokenAt) Verify() => Verify(Entries);

    public static (bool Valid, long? BrokenAt) Verify(IReadOnlyList<AuditEntry> entries)
    {
        var prev = Genesis;
        foreach (var e in entries)
        {
            if (e.PrevHash != prev || e.Hash != ComputeHash(prev, e.Seq, e.Ts, e.Type, e.NodeId, e.Actor, e.Message, e.Data)) return (false, e.Seq);
            prev = e.Hash;
        }
        return (true, null);
    }

    private static string ComputeHash(string prev, long seq, DateTimeOffset ts, string type, string? nodeId, string actor, string message, JsonNode data) =>
        Json.Sha256Hex($"{prev}|{seq}|{ts:O}|{type}|{nodeId}|{actor}|{message}|{Json.Canonical(data)}");
}
