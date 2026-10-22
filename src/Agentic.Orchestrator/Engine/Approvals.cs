namespace Agentic.Orchestrator;

/// <summary>A human checkpoint. Kind = approval | clarification. When = before | after (relative to the node's work).</summary>
public sealed record ApprovalRequest(string Id, string RunId, string NodeId, string Kind, string When, string Risk, string Reason, string Summary, List<OpenQuestion> Questions, DateTimeOffset RequestedAt);

public sealed record ApprovalDecision(bool Approved, string Approver, string Comment, Dictionary<string, string>? Answers = null);

/// <summary>Human-in-the-loop port. Production = the Angular console (queue); demos/tests = scripted approver.</summary>
public interface IApprovalProvider
{
    Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Simulated human for demos and tests: approves everything unless the scenario says "reject" for that node id,
/// and supplies clarification answers from the scenario script. Every decision is audited with the approver "scripted:&lt;scenario&gt;".
/// </summary>
public sealed class ScriptedApprovalProvider(ScenarioSpec scenario) : IApprovalProvider
{
    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
    {
        var who = $"scripted:{scenario.Id}";
        if (request.Kind == "clarification")
        {
            var answers = request.Questions.Where(q => scenario.Answers.ContainsKey(QuestionKey(q))).ToDictionary(QuestionKey, q => scenario.Answers[QuestionKey(q)]);
            return Task.FromResult(answers.Count > 0
                ? new ApprovalDecision(true, who, $"Answered {answers.Count} question(s) from the scenario script", answers)
                : new ApprovalDecision(false, who, "No scripted answers available"));
        }
        var reject = scenario.Approvals.TryGetValue(request.NodeId, out var v) && v.Equals("reject", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new ApprovalDecision(!reject, who, reject ? "Rejected by scenario script" : "Reviewed summary; within autonomy policy"));
    }

    private static string QuestionKey(OpenQuestion q) => q.Id;
}

/// <summary>Queue-backed provider: requests wait until someone (the REST API / Angular console) resolves them.</summary>
public sealed class QueueApprovalProvider : IApprovalProvider
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<ApprovalDecision>> _waiting = new();

    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _waiting[request.Id] = tcs;
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public bool Resolve(string requestId, ApprovalDecision decision)
    {
        TaskCompletionSource<ApprovalDecision>? tcs;
        lock (_lock) { if (!_waiting.Remove(requestId, out tcs)) return false; }
        return tcs.TrySetResult(decision);
    }
}
