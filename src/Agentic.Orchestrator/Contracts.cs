using System.Text.Json.Nodes;

namespace Agentic.Orchestrator;

// ───────────────────────────── Execution model ─────────────────────────────

public enum NodeStatus { Pending, Running, AwaitingApproval, Done, Failed, Blocked, Skipped, Stale, Cancelled, Rejected }

public enum RunStatus { Running, AwaitingHuman, Completed, SafeStopped, Rejected, Failed }

/// <summary>Bounded retry policy: <c>MaxRetries</c> extra attempts after the first, exponential backoff.</summary>
public sealed record RetryPolicy(int MaxRetries = 2, int BackoffMs = 50);

/// <summary>Human approval checkpoint. When = "before" (gate the work) or "after" (gate the commit of the result).</summary>
public sealed record ApprovalSpec(string When, string Risk, string Reason);

/// <summary>Substitute agent used once after retries are exhausted.</summary>
public sealed record FallbackSpec(string Agent, Dictionary<string, string>? Params = null);

/// <summary>A node of the SDLC dependency graph (serialisable; gates/agents are referenced by name).</summary>
public sealed class NodeSpec
{
    public required string Id { get; set; }
    public string Stage { get; set; } = "";
    public required string Agent { get; set; }
    public List<string> Deps { get; set; } = new();
    /// <summary>Artifacts this node may read (least privilege) – also defines its input hash for re-planning.</summary>
    public List<string> Reads { get; set; } = new();
    public List<string> EntryGates { get; set; } = new();
    public List<string> ExitGates { get; set; } = new();
    public Dictionary<string, string> Params { get; set; } = new();
    public RetryPolicy Retry { get; set; } = new();
    public FallbackSpec? Fallback { get; set; }
    public ApprovalSpec? Approval { get; set; }
    /// <summary>null | "workspace:empty" | "workspace:baseline" – node is Skipped when the condition is false.</summary>
    public string? Condition { get; set; }
    public bool Rollback { get; set; } = true;
    /// <summary>Id of the node whose expansion created this node (dynamic planning).</summary>
    public string? ExpandedBy { get; set; }
}

/// <summary>Dynamic plan expansion returned by an agent (e.g. architect → one implement node per feature).</summary>
public sealed record Expansion(List<NodeSpec> Nodes, List<string> JoinInto);

public sealed record Decision(string Title, string Rationale, List<string> Alternatives, string Tradeoff);

public sealed class AgentResult
{
    public Dictionary<string, JsonNode> Artifacts { get; } = new();
    public List<Decision> Decisions { get; } = new();
    public Expansion? Expansion { get; set; }
    public string? Notes { get; set; }

    public AgentResult Add(string artifactName, object value) { Artifacts[artifactName] = Json.ToNode(value); return this; }
    public AgentResult Decide(string title, string rationale, IEnumerable<string>? alternatives = null, string tradeoff = "")
    { Decisions.Add(new Decision(title, rationale, alternatives?.ToList() ?? new(), tradeoff)); return this; }
}

/// <summary>What an agent is allowed to do. Enforced by the sandboxed workspace capability, not by trust.</summary>
public sealed record AutonomyBoundary(string[] WriteGlobs, bool CanRunTests = false, bool ReadsRunSnapshot = false);

public interface IAgent
{
    string Name { get; }
    AutonomyBoundary Autonomy { get; }
    Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct);
}

// ───────────────────────────── Workspace sandbox ─────────────────────────────

public sealed record FileChange(string Path, string Op, string Sha256, long Bytes);

/// <summary>Sandboxed, journaled view of the generated workspace handed to an agent. Paths are workspace-relative, '/'-separated.</summary>
public interface IWorkspace
{
    string Root { get; }
    bool Exists(string rel);
    string ReadText(string rel);
    /// <summary>Glob (supports ** and *), returns workspace-relative paths. Excludes bin/ obj/.</summary>
    IReadOnlyList<string> List(string glob = "**/*");
    /// <summary>Write (create or overwrite). Throws <see cref="AutonomyViolationException"/> if outside the agent's write boundary or outside the sandbox.</summary>
    FileChange WriteText(string rel, string content);
    FileChange CopyFrom(string absoluteSourcePath, string rel);
    void Remove(string rel);
    string Sha256(string rel);
    /// <summary>Copy the whole workspace (minus bin/obj) into an empty absolute directory – used for non-destructive experiments such as defect reproduction.</summary>
    void CopyTo(string absoluteDestDir);
    /// <summary>Files changed by the current node so far.</summary>
    IReadOnlyList<FileChange> ChangedFiles { get; }
}

public sealed class AutonomyViolationException(string message) : Exception(message);

public interface ITestRunner
{
    /// <summary>Runs <c>dotnet test</c> for the project in the given workspace root. Never throws for test failures – they are reported.</summary>
    Task<TestReport> RunAsync(string workspaceRoot, string testProjectRelPath, string? filter, TimeSpan timeout, CancellationToken ct);
}

// ───────────────────────────── Agent context ─────────────────────────────

public interface IRunSnapshot
{
    /// <summary>Metrics as of now (summariser/release-manager only).</summary>
    JsonNode Metrics();
    /// <summary>Node states, approvals, policy violations, replans, retries… as JSON.</summary>
    JsonNode State();
    JsonNode Lineage();
    JsonNode Audit();
}

public sealed class AgentContext
{
    public required NodeSpec Node { get; init; }
    public IReadOnlyDictionary<string, string> Params => Node.Params;
    /// <summary>1-based attempt number (resets per agent: primary, then fallback).</summary>
    public int Attempt { get; init; } = 1;
    public bool IsFallback { get; init; }
    public required ScenarioSpec Scenario { get; init; }
    public required IWorkspace Workspace { get; init; }
    public required FeatureCatalog Catalog { get; init; }
    public required ITestRunner Tests { get; init; }
    public required TimeProvider Time { get; init; }
    public IRunSnapshot? Snapshot { get; init; }
    public required Func<string, JsonNode?> ArtifactReader { get; init; }
    public required Action<string> LogSink { get; init; }
    /// <summary>Absolute directory where agents may write run-level (non-workspace) outputs, e.g. ENGINEERING_SUMMARY.md.</summary>
    public required string RunDirectory { get; init; }

    public bool Has(string artifactName) => ArtifactReader(artifactName) is not null;
    public T Read<T>(string artifactName) =>
        (ArtifactReader(artifactName) ?? throw new InvalidOperationException($"Artifact '{artifactName}' is not available to node '{Node.Id}'.")).Deserialize<T>() ??
        throw new InvalidOperationException($"Artifact '{artifactName}' is empty.");
    public T? ReadOrDefault<T>(string artifactName) where T : class => ArtifactReader(artifactName) is { } n ? n.Deserialize<T>() : null;
    public string Param(string key, string fallback = "") => Node.Params.TryGetValue(key, out var v) ? v : fallback;
    public void Log(string message) => LogSink(message);
}

// ───────────────────────────── Scenario ─────────────────────────────

/// <summary>Chaos fault: makes <c>Agent</c> misbehave on the given 1-based attempts of <c>Node</c>. Mode: throw | bad-output | critical-policy.</summary>
public sealed record FaultSpec(string Node, string Agent, int[] Attempts, string Mode);

/// <summary>Mid-run requirement change that triggers dynamic re-planning once <c>AfterNode</c> is Done.</summary>
public sealed record ScenarioEvent(string AfterNode, string Description, string[] AddFeatures, string[] RemoveFeatures, string? AppendText);

public sealed class ScenarioSpec
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    /// <summary>greenfield | brownfield | ambiguous</summary>
    public required string Mode { get; set; }
    public string Description { get; set; } = "";
    public required string RequirementText { get; set; }
    /// <summary>empty | baseline  (baseline = seeded v1 codebase with known defects)</summary>
    public string Workspace { get; set; } = "empty";
    /// <summary>Scripted human clarification answers keyed by ambiguity id (used by the scripted approver in demos/tests).</summary>
    public Dictionary<string, string> Answers { get; set; } = new();
    /// <summary>Scripted approver overrides keyed by node id: "approve" | "reject". Default = approve.</summary>
    public Dictionary<string, string> Approvals { get; set; } = new();
    public List<FaultSpec> Faults { get; set; } = new();
    public List<ScenarioEvent> Events { get; set; } = new();
}
