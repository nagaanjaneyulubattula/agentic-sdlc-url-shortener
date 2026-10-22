using System.Text.Json.Nodes;

namespace Agentic.Orchestrator;

/// <summary>JSON views of a run for the REST API / Angular console and for agents (<see cref="IRunSnapshot"/>).</summary>
public static class RunViews
{
    public static object Build(Engine e)
    {
        var s = e.State;
        var graph = e.BuildGraph();
        var m = e.Metrics();
        List<object> nodes;
        List<object> pending;
        List<object> replans;
        lock (s.Sync)
        {
            nodes = s.Nodes.Values.Where(n => n.Status != NodeStatus.Cancelled || n.Attempts > 0).Select(n => (object)new
            {
                id = n.Spec.Id, stage = n.Spec.Stage, agent = n.Spec.Agent, deps = n.Spec.Deps.Where(d => s.Nodes.ContainsKey(d) && s.Nodes[d].Status != NodeStatus.Cancelled).ToList(),
                status = n.Status.ToString(), attempts = n.Attempts, retries = n.Retries, rollbacks = n.Rollbacks, usedFallback = n.UsedFallback,
                startedAt = n.StartedAt, endedAt = n.EndedAt, durationMs = n.DurationMs, error = n.Error, reads = n.Spec.Reads, expandedBy = n.Spec.ExpandedBy,
                approval = n.Spec.Approval is null ? null : new { when = n.Spec.Approval.When, risk = n.Spec.Approval.Risk, reason = n.Spec.Approval.Reason },
            }).ToList();
            pending = s.Pending.Select(p => (object)new
            {
                id = p.Id, nodeId = p.NodeId, kind = p.Kind, when = p.When, risk = p.Risk, reason = p.Reason, summary = p.Summary,
                questions = p.Questions.Select(q => new { id = q.Id, question = q.Question }), requestedAt = p.RequestedAt,
            }).ToList();
            replans = s.Replans.Select(r => (object)new { at = r.At, reason = r.Reason, staleNodes = r.StaleNodes }).ToList();
        }
        return new
        {
            id = s.RunId, scenarioId = s.Scenario.Id, title = s.Scenario.Title, mode = s.Scenario.Mode, status = s.Status.ToString(),
            stopReason = s.StopReason, startedAt = s.StartedAt, endedAt = s.EndedAt,
            nodes, layers = graph.Layers(), pendingApprovals = pending,
            metrics = new
            {
                m.SuccessRate, m.AttemptSuccessRate, m.TotalAttempts, m.Retries, m.Rollbacks, m.Fallbacks, m.Replans, m.PolicyViolations,
                m.ApprovalsRequested, m.ApprovalsGranted, m.ApprovalsRejected, m.MttrMs, m.EndToEndMs, m.CriticalPathMs, m.CriticalPath,
                m.NodesDone, m.NodesTotal, m.ApprovalWaitMs, m.PerStage,
            },
            replans,
        };
    }

    public static object AuditView(Engine e)
    {
        var entries = e.Audit.Entries;
        var (valid, broken) = AuditLog.Verify(entries);
        return new
        {
            entries = entries.Select(x => new { seq = x.Seq, ts = x.Ts, type = x.Type, nodeId = x.NodeId, actor = x.Actor, message = x.Message, data = x.Data, prevHash = x.PrevHash, hash = x.Hash }),
            chainValid = valid, brokenAt = broken, headHash = e.Audit.HeadHash,
        };
    }

    public static object Lineage(Engine e)
    {
        lock (e.State.Sync)
            return e.State.Decisions.Select(d => new
            {
                id = d.Id, nodeId = d.NodeId, title = d.Title, rationale = d.Rationale, alternatives = d.Alternatives, tradeoff = d.Tradeoff,
                basedOn = d.BasedOn.Select(b => new { artifact = b.Artifact, hash = b.Hash }), at = d.At,
            }).ToList();
    }

    public static object ArtifactIndex(Engine e) =>
        e.State.Artifacts.LatestMetas().Select(a => new { name = a.Name, version = a.Version, hash = a.Hash, bytes = a.Bytes, producer = a.Producer }).ToList();

    public static JsonNode StateForAgents(Engine e)
    {
        var s = e.State;
        lock (s.Sync)
        {
            return Json.ToNode(new
            {
                status = s.Status.ToString(),
                nodes = s.Nodes.Values.Select(n => new { id = n.Spec.Id, stage = n.Spec.Stage, agent = n.Spec.Agent, status = n.Status.ToString(), attempts = n.Attempts, retries = n.Retries, rollbacks = n.Rollbacks, usedFallback = n.UsedFallback, durationMs = n.DurationMs, error = n.Error }),
                approvals = s.Approvals.Select(a => new { nodeId = a.NodeId, kind = a.Kind, when = a.When, decision = a.Decision, approver = a.Approver, comment = a.Comment, risk = a.Risk, reason = a.Reason }),
                policyViolations = s.Violations.Select(v => new { rule = v.Rule, severity = v.Severity, nodeId = v.NodeId, file = v.File, message = v.Message }),
                replans = s.Replans.Select(r => new { at = r.At, reason = r.Reason, staleNodes = r.StaleNodes }),
                changes = s.Changes.Select(c => new { path = c.Path, op = c.Op, nodeId = c.NodeId }),
                artifacts = s.Artifacts.LatestMetas().Select(a => new { name = a.Name, version = a.Version, hash = a.Hash }),
                mermaid = e.BuildGraph().ToMermaid(id => s.Nodes.TryGetValue(id, out var n) ? n.Status.ToString() : ""),
            });
        }
    }
}

/// <summary>Read-only run snapshot handed to agents (release manager / summariser).</summary>
internal sealed class RunSnapshot(Engine e) : IRunSnapshot
{
    public JsonNode Metrics() => Json.ToNode(e.Metrics());
    public JsonNode State() => RunViews.StateForAgents(e);
    public JsonNode Lineage() => Json.ToNode(RunViews.Lineage(e));
    public JsonNode Audit() => Json.ToNode(RunViews.AuditView(e));
}
