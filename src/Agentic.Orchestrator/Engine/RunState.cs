using System.Text.Json.Nodes;

namespace Agentic.Orchestrator;

public sealed class NodeState
{
    public required NodeSpec Spec { get; set; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public int Attempts { get; set; }
    public int Retries { get; set; }
    public int Rollbacks { get; set; }
    public bool UsedFallback { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double ApprovalWaitMs { get; set; }
    public double? DurationMs { get; set; }
    public string? Error { get; set; }
    /// <summary>Ledger of the artifact hashes this node's last execution consumed (content-addressed invalidation).</summary>
    public Dictionary<string, string> InputHashes { get; set; } = new();
    public int Generation { get; set; }
    public int CompletionSeq { get; set; }
    public bool Superseded { get; set; }
    public DateTimeOffset? FirstFailureAt { get; set; }
}

public sealed record ArtifactMeta(string Name, int Version, string Hash, string Producer, DateTimeOffset At, int Bytes);
public sealed record ArtifactRef(string Artifact, string Hash);
public sealed record DecisionRecord(string Id, string NodeId, string Title, string Rationale, List<string> Alternatives, string Tradeoff, List<ArtifactRef> BasedOn, DateTimeOffset At);
public sealed record ApprovalRecord(string Id, string NodeId, string Kind, string When, string Risk, string Reason, string Decision, string Approver, string Comment, DateTimeOffset At, double WaitMs);
public sealed record PolicyViolation(string Rule, string Severity, string NodeId, string File, string Message, DateTimeOffset At);
public sealed record ReplanRecord(DateTimeOffset At, string Reason, List<string> StaleNodes);
public sealed record ChangeRecord(string Path, string Op, string NodeId);

public sealed class ArtifactStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(ArtifactMeta Meta, JsonNode Value)>> _items = new(StringComparer.Ordinal);

    public ArtifactMeta Put(string name, JsonNode value, string producer, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(name, out var list)) _items[name] = list = new();
            var hash = Json.Hash(value);
            var meta = new ArtifactMeta(name, list.Count + 1, hash, producer, at, System.Text.Encoding.UTF8.GetByteCount(Json.Canonical(value)));
            list.Add((meta, value.DeepClone()));
            return meta;
        }
    }

    public JsonNode? Latest(string name) { lock (_lock) return _items.TryGetValue(name, out var l) ? l[^1].Value.DeepClone() : null; }
    public string? LatestHash(string name) { lock (_lock) return _items.TryGetValue(name, out var l) ? l[^1].Meta.Hash : null; }
    public IReadOnlyList<ArtifactMeta> LatestMetas() { lock (_lock) return _items.Values.Select(l => l[^1].Meta).OrderBy(m => m.Name, StringComparer.Ordinal).ToList(); }
    public IReadOnlyList<ArtifactMeta> History() { lock (_lock) return _items.Values.SelectMany(l => l.Select(x => x.Meta)).OrderBy(m => m.At).ToList(); }
}

public sealed class RunState
{
    public required string RunId { get; init; }
    public required ScenarioSpec Scenario { get; init; }
    public required string RunDirectory { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public string? StopReason { get; set; }
    public bool Halted { get; set; }
    public bool RejectedByHuman { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Dictionary<string, NodeState> Nodes { get; } = new(StringComparer.Ordinal);
    public ArtifactStore Artifacts { get; } = new();
    public List<DecisionRecord> Decisions { get; } = new();
    public List<ApprovalRecord> Approvals { get; } = new();
    public List<ApprovalRequest> Pending { get; } = new();
    public List<PolicyViolation> Violations { get; } = new();
    public List<ReplanRecord> Replans { get; } = new();
    public List<ChangeRecord> Changes { get; } = new();
    public HashSet<int> FiredEvents { get; } = new();
    public int AttemptsTotal { get; set; }
    public int AttemptsFailed { get; set; }
    public int TotalRetries { get; set; }
    public List<double> RecoveryMs { get; } = new();
    public int CompletionCounter { get; set; }
    public int DecisionCounter { get; set; }
    public object Sync { get; } = new();
}
