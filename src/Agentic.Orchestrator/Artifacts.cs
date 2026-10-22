namespace Agentic.Orchestrator;

// Typed artifact schemas exchanged between agents (stored as JSON, camelCase, content-hashed).

// requirements ─────────────────────────────────────────────
/// <summary>Source: explicit | implied | clarified | amended</summary>
public sealed record FeatureReq(string Id, string Title, string Source, string Rationale);
public sealed record Nfr(string Id, string Category, string Statement, string? Target, string Source);
/// <summary>Severity: high | medium | low</summary>
public sealed record Ambiguity(string Id, string Term, string Evidence, string Severity, string Question, string AssumedDefault, bool Resolved, string? Resolution);
public sealed record OpenQuestion(string Id, string Question);
/// <summary>Status: clear | needs-clarification. Features hold catalog feature ids (always including shorten+redirect for greenfield).</summary>
public sealed record RequirementsDoc(
    string RawText, string ProblemStatement, List<string> Actors, string Status,
    List<FeatureReq> Features, List<Nfr> Nfrs, List<Ambiguity> Ambiguities,
    List<string> Assumptions, List<OpenQuestion> OpenQuestions, Dictionary<string, string> Answers, int Revision);

// impact / repro (brownfield) ────────────────────────────────
public sealed record RouteInfo(string Method, string Path, string File);
public sealed record ModuleInfo(string Path, List<string> Usings, List<string> Types, List<RouteInfo> Routes, List<string> Hooks);
/// <summary>Change: modify | extend. Risk: low | medium | high.</summary>
public sealed record Impacted(string Path, string Reason, string Change, string Risk);
/// <summary>Severity: critical | high | medium | low.</summary>
public sealed record Finding(string Id, string Severity, string? Cwe, string File, int Line, string Description, string Fix);
public sealed record ImpactReport(
    List<string> InstalledFeatures, List<ModuleInfo> Modules, Dictionary<string, List<string>> DependencyGraph,
    List<RouteInfo> Routes, List<Impacted> Impacted, List<string> NewFiles, List<string> ModifiedFiles,
    List<string> ApiChanges, List<string> DataFlows, List<Finding> Findings, TestReport? BaselineTests);
public sealed record ReproReport(bool Reproduced, int FailingTests, int TotalTests, List<string> Failures, string Summary);

// design ─────────────────────────────────────────────────────
public sealed record Adr(string Id, string Title, string Status, string Context, string Decision, List<string> Alternatives, string Consequences);
public sealed record RiskItem(string Id, string Scenario, string Likelihood, string Impact, string Mitigation, List<string> Validation, string Residual);
/// <summary>Kind: slice | patch. Id is the graph node id ("implement:&lt;feature&gt;"); also published as artifact "task:&lt;feature&gt;".</summary>
public sealed record TaskSpec(string Id, string Feature, string Kind, List<string> DependsOn, List<string> ModifiesExisting, bool Sensitive, string? SensitiveReason);
public sealed record ComponentInfo(string Id, string Path, string Responsibility);
/// <summary>Features = full target feature set in dependency/order sequence; ToInstall = those still to implement; AlreadyInstalled = present in the brownfield baseline.</summary>
public sealed record DesignDoc(
    string Mode, List<string> Features, List<string> ToInstall, List<string> AlreadyInstalled,
    List<ComponentInfo> Components, string DataModel, System.Text.Json.Nodes.JsonNode OpenApi,
    List<Adr> Adrs, List<RiskItem> Risks, List<TaskSpec> Tasks);

// test plan / implementation / verification ─────────────────
public sealed record TestPlan(List<string> Levels, Dictionary<string, List<AcceptanceCriterion>> AcceptanceByFeature, List<string> RequiredAcceptanceIds, List<string> Gates);
/// <summary>Op: create | modify | delete | unchanged</summary>
public sealed record ImplReport(string Feature, List<FileChange> Files, string Strategy);
public sealed record IntegrationReport(List<string> InstalledFeatures, string RegistryPath, List<FileChange> Files);
public sealed record TestReport(int Passed, int Failed, int Skipped, int Total, long DurationMs, int ExitCode, List<string> Failures, string OutputTail)
{
    public bool Green => ExitCode == 0 && Failed == 0 && Total > 0;
}
public sealed record TraceRow(string AcceptanceId, string Feature, List<string> Tests, bool Covered);
public sealed record Traceability(List<TraceRow> Matrix, double Coverage, List<string> Missing);
/// <summary>Severity: critical | block | warn</summary>
public sealed record SecurityFinding(string Rule, string Severity, string File, int Line, string Message);
public sealed record SecurityReport(int ScannedFiles, List<SecurityFinding> Findings, bool Passed);
public sealed record DocsReport(List<FileChange> Files);
/// <summary>Status: pass | fail | warn</summary>
public sealed record ChecklistItem(string Id, string Title, string Status, string Evidence);
/// <summary>Decision: GO | NO-GO</summary>
public sealed record ReleaseReadiness(string Decision, List<ChecklistItem> Checklist, int FilesCreated, int FilesModified, List<string> RollbackPlan, List<string> ResidualRisks);
