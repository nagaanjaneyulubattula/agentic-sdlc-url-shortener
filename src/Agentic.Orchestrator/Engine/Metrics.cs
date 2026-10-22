namespace Agentic.Orchestrator;

public sealed record MetricsView(
    double SuccessRate, double AttemptSuccessRate, int TotalAttempts, int Retries, int Rollbacks, int Fallbacks, int Replans,
    int PolicyViolations, int ApprovalsRequested, int ApprovalsGranted, int ApprovalsRejected, double? MttrMs,
    double EndToEndMs, double CriticalPathMs, List<string> CriticalPath, int NodesDone, int NodesTotal, double ApprovalWaitMs,
    Dictionary<string, double> PerStage);

/// <summary>Reliability metrics derived from the run state: success rate, retry/rollback frequency, MTTR and end-to-end latency.</summary>
public static class MetricsCalculator
{
    public static MetricsView Compute(RunState s, DependencyGraph graph, DateTimeOffset now)
    {
        List<NodeState> nodes;
        lock (s.Sync) nodes = s.Nodes.Values.Where(n => n.Status != NodeStatus.Cancelled).ToList();

        var done = nodes.Count(n => n.Status == NodeStatus.Done);
        var failedTerminal = nodes.Count(n => n.Status is NodeStatus.Failed or NodeStatus.Rejected or NodeStatus.Blocked);
        var executed = done + failedTerminal;
        var successRate = executed == 0 ? 1.0 : (double)done / executed;
        var attemptRate = s.AttemptsTotal == 0 ? 1.0 : (double)(s.AttemptsTotal - s.AttemptsFailed) / s.AttemptsTotal;
        var approvals = s.Approvals.Where(a => a.Kind == "approval").ToList();

        var perStage = nodes.Where(n => n.DurationMs.HasValue)
            .GroupBy(n => string.IsNullOrEmpty(n.Spec.Stage) ? "other" : n.Spec.Stage)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(n => n.DurationMs!.Value), 1));

        (double total, List<string> path) cp = (0, new());
        try { cp = graph.CriticalPath(id => s.Nodes.TryGetValue(id, out var n) && n.Status == NodeStatus.Done ? n.DurationMs ?? 0 : 0); }
        catch (GraphException) { /* graph under mutation – report 0 */ }

        return new MetricsView(
            Math.Round(successRate, 4), Math.Round(attemptRate, 4), s.AttemptsTotal,
            nodes.Sum(n => n.Retries), nodes.Sum(n => n.Rollbacks), nodes.Count(n => n.UsedFallback), s.Replans.Count,
            s.Violations.Count, approvals.Count + s.Pending.Count(p => p.Kind == "approval"),
            approvals.Count(a => a.Decision == "approved"), approvals.Count(a => a.Decision == "rejected"),
            s.RecoveryMs.Count == 0 ? null : Math.Round(s.RecoveryMs.Average(), 1),
            Math.Round(((s.EndedAt ?? now) - s.StartedAt).TotalMilliseconds, 1),
            Math.Round(cp.total, 1), cp.path, done, nodes.Count(n => n.Status != NodeStatus.Skipped),
            Math.Round(nodes.Sum(n => n.ApprovalWaitMs), 1), perStage);
    }
}
