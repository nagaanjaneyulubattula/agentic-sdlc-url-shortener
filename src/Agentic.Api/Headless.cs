using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;

/// <summary>
/// Headless runner: <c>dotnet run --project src/Agentic.Api -- run &lt;scenario&gt; [--manual]</c>.
/// Streams the audit trail live; approvals are scripted by default, or asked on the console with --manual.
/// </summary>
public static class Headless
{
    public static async Task<int> RunAsync(string[] args)
    {
        var catalog = FeatureCatalog.Load();
        var scenarios = ScenarioLoader.LoadAll(catalog.RepoRoot);
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        var scenario = scenarios.FirstOrDefault(s => s.Id == id);
        if (scenario is null)
        {
            Console.Error.WriteLine($"Usage: run <scenario> [--manual]\nScenarios: {string.Join(", ", scenarios.Select(s => s.Id))}");
            return 2;
        }
        scenario = ScenarioLoader.Clone(scenario);
        var manual = args.Contains("--manual");
        var engine = new Engine(scenario, catalog, DefaultAgents.All(), new EngineOptions
        {
            RunsRoot = Path.Combine(catalog.RepoRoot, "runs"),
            TestRunner = new DotnetTestRunner(),
            Approvals = manual ? new ConsoleApprovalProvider() : new ScriptedApprovalProvider(scenario),
        });
        Console.WriteLine($"=== {scenario.Title}\n=== run {engine.State.RunId}  ({(manual ? "manual approvals" : "scripted approver")})\n=== requirement: {scenario.RequirementText}\n");

        var run = engine.RunAsync();
        var printed = 0;
        while (!run.IsCompleted)
        {
            printed = Print(engine, printed);
            await Task.WhenAny(run, Task.Delay(200));
        }
        var result = await run;
        Print(engine, printed);

        var m = engine.Metrics();
        Console.WriteLine($"\n=== RESULT: {result.Status}{(result.StopReason is null ? "" : " – " + result.StopReason)}   release decision: {result.ReleaseDecision ?? "n/a"}");
        Console.WriteLine($"=== nodes {m.NodesDone}/{m.NodesTotal} done | success rate {m.SuccessRate:P0} | attempt success {m.AttemptSuccessRate:P0} | retries {m.Retries} | rollbacks {m.Rollbacks} | fallbacks {m.Fallbacks} | replans {m.Replans} | policy violations {m.PolicyViolations}");
        Console.WriteLine($"=== MTTR {(m.MttrMs is null ? "n/a" : m.MttrMs + " ms")} | end-to-end {m.EndToEndMs / 1000:0.0}s (human wait {m.ApprovalWaitMs / 1000:0.0}s) | critical path {string.Join(" → ", m.CriticalPath)}");
        Console.WriteLine($"=== audit chain valid: {engine.Audit.Verify().Valid}   head {engine.Audit.HeadHash[..16]}…");
        Console.WriteLine($"=== workspace: {result.WorkspaceRoot}\n=== summary:   {Path.Combine(result.RunDirectory, "ENGINEERING_SUMMARY.md")}");
        return result.Status == RunStatus.Completed ? 0 : 1;
    }

    private static int Print(Engine e, int from)
    {
        var entries = e.Audit.Entries;
        for (var i = from; i < entries.Count; i++)
        {
            var x = entries[i];
            if (x.Type is "agent.log" or "attempt.started" or "artifact.committed") continue;
            Console.WriteLine($"{x.Ts:HH:mm:ss.fff} {x.Type,-20} {(x.NodeId ?? "-"),-26} {x.Message}");
        }
        return entries.Count;
    }
}

/// <summary>Asks a human on the terminal.</summary>
public sealed class ConsoleApprovalProvider : IApprovalProvider
{
    private readonly object _lock = new();

    public Task<ApprovalDecision> DecideAsync(ApprovalRequest r, CancellationToken ct) => Task.Run(() =>
    {
        lock (_lock)
        {
            Console.WriteLine($"\n┌── {r.Kind.ToUpperInvariant()} REQUIRED [{r.Risk}] node '{r.NodeId}' ({r.When})\n│ {r.Reason}");
            foreach (var line in r.Summary.Split('\n')) Console.WriteLine("│ " + line);
            if (r.Kind == "clarification")
            {
                var answers = new Dictionary<string, string>();
                foreach (var q in r.Questions)
                {
                    Console.Write($"│ {q.Id}: {q.Question}\n│ answer> ");
                    var a = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(a)) answers[q.Id] = a;
                }
                return new ApprovalDecision(answers.Count > 0, Environment.UserName, "answered on console", answers);
            }
            Console.Write("└── approve? [y/N] ");
            var ok = Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
            Console.Write("    comment> ");
            return new ApprovalDecision(ok, Environment.UserName, Console.ReadLine() ?? "");
        }
    }, ct);
}
