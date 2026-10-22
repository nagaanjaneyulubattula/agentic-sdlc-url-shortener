using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Agentic.Orchestrator;

public sealed record GateHuman(string Kind, List<OpenQuestion> Questions, string Summary);
public sealed record GateResult(bool Ok, string Reason, GateHuman? Human = null)
{
    public static GateResult Pass(string reason = "ok") => new(true, reason);
    public static GateResult Fail(string reason) => new(false, reason);
}

public sealed class GateContext
{
    public required NodeState Node { get; init; }
    public AgentResult? Result { get; init; }
    public required IWorkspace Workspace { get; init; }
    public required FeatureCatalog Catalog { get; init; }
    public required Func<string, JsonNode?> Artifact { get; init; }
    public IReadOnlyList<FileChange> Changes => Workspace.ChangedFiles;
    public T? Get<T>(string name) where T : class => Artifact(name) is { } n ? n.Deserialize<T>() : null;
}

public interface IGate
{
    string Name { get; }
    Task<GateResult> CheckAsync(GateContext ctx);
}

internal sealed class DelegateGate(string name, Func<GateContext, GateResult> check) : IGate
{
    public string Name => name;
    public Task<GateResult> CheckAsync(GateContext ctx) => Task.FromResult(check(ctx));
}

/// <summary>Named entry/exit gates. Nodes reference them by name so the plan stays serialisable.</summary>
public static class Gates
{
    public static IReadOnlyDictionary<string, IGate> CreateDefault()
    {
        var list = new List<IGate>
        {
            new DelegateGate("workspace-ready", c =>
                File.Exists(Path.Combine(c.Workspace.Root, c.Catalog.Core.TestProject)) ? GateResult.Pass("core project present")
                    : GateResult.Fail("workspace is not scaffolded (test project missing)")),

            new DelegateGate("requirements-valid", c =>
            {
                var r = c.Get<RequirementsDoc>("requirements");
                if (r is null) return GateResult.Fail("requirements artifact missing");
                if (string.IsNullOrWhiteSpace(r.ProblemStatement)) return GateResult.Fail("empty problem statement");
                if (r.Features.Count < 2) return GateResult.Fail("fewer than the 2 core features");
                return GateResult.Pass($"{r.Features.Count} features, {r.Ambiguities.Count} ambiguities");
            }),

            new DelegateGate("requirements-clear", c =>
            {
                var r = c.Get<RequirementsDoc>("requirements");
                if (r is null) return GateResult.Fail("requirements artifact missing");
                if (r.Status == "clear") return GateResult.Pass("no unresolved high-severity ambiguity");
                var high = r.Ambiguities.Where(a => !a.Resolved && a.Severity == "high").Select(a => a.Id).ToHashSet();
                var qs = r.OpenQuestions.Where(q => high.Contains(q.Id)).ToList();
                var summary = "The agent refuses to guess. Unresolved high-severity ambiguities:\n" +
                              string.Join("\n", r.Ambiguities.Where(a => !a.Resolved && a.Severity == "high")
                                  .Select(a => $"  - {a.Id} ('{a.Evidence}'): assumed default would be \"{a.AssumedDefault}\""));
                return new GateResult(false, "unresolved high-severity ambiguities", new GateHuman("clarification", qs, summary));
            }),

            new DelegateGate("design-valid", c =>
            {
                var d = c.Get<DesignDoc>("design");
                if (d is null) return GateResult.Fail("design artifact missing");
                if (d.Tasks.Count != d.ToInstall.Count) return GateResult.Fail("task count does not match features to install");
                if (c.Result?.Expansion is null && d.Tasks.Count > 0) return GateResult.Fail("design produced no plan expansion");
                if (d.Risks.Count == 0) return GateResult.Fail("risk register is empty");
                if (d.Adrs.Count == 0) return GateResult.Fail("no architecture decisions recorded");
                var missing = d.Tasks.Where(t => c.Result is not null && !c.Result.Artifacts.ContainsKey("task:" + t.Feature)).Select(t => t.Feature).ToList();
                return missing.Count > 0 ? GateResult.Fail("missing task artifacts: " + string.Join(", ", missing))
                    : GateResult.Pass($"{d.Tasks.Count} tasks, {d.Risks.Count} risks, {d.Adrs.Count} ADRs");
            }),

            new DelegateGate("files-written", c =>
            {
                var ok = c.Result?.Artifacts.Values.Any(a => a is JsonObject o && o["files"] is JsonArray { Count: > 0 }) == true;
                return ok ? GateResult.Pass($"{c.Changes.Count} file change(s)") : GateResult.Fail("agent reported no files");
            }),

            // Policy scanning is enforced engine-wide after every node; this named gate documents it in the plan.
            new DelegateGate("policy-clean", _ => GateResult.Pass("enforced by engine-wide policy guardrail")),

            new DelegateGate("syntax-valid", c =>
            {
                var errors = new List<string>();
                foreach (var f in c.Changes.Where(f => f.Path.EndsWith(".cs") && f.Op != "delete"))
                {
                    var tree = CSharpSyntaxTree.ParseText(c.Workspace.ReadText(f.Path), path: f.Path);
                    errors.AddRange(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(3).Select(d => $"{f.Path}: {d.GetMessage()} @ {d.Location.GetLineSpan().StartLinePosition}"));
                }
                return errors.Count == 0 ? GateResult.Pass("all changed C# files parse") : GateResult.Fail(string.Join("; ", errors.Take(5)));
            }),

            new DelegateGate("tests-green", c =>
            {
                var t = c.Get<TestReport>("test-report");
                if (t is null) return GateResult.Fail("test-report missing");
                return t.Green ? GateResult.Pass($"{t.Passed}/{t.Total} tests passed")
                    : GateResult.Fail($"tests not green: {t.Failed} failed, {t.Total} total, exit {t.ExitCode}. {string.Join("; ", t.Failures.Take(3))}");
            }),

            new DelegateGate("traceability-complete", c =>
            {
                var t = c.Get<Traceability>("traceability");
                if (t is null) return GateResult.Fail("traceability missing");
                return t.Coverage >= 1.0 ? GateResult.Pass($"{t.Matrix.Count} acceptance criteria all covered")
                    : GateResult.Fail($"acceptance criteria without tests: {string.Join(", ", t.Missing)}");
            }),

            new DelegateGate("security-passed", c =>
            {
                var s = c.Get<SecurityReport>("security-report");
                if (s is null) return GateResult.Fail("security-report missing");
                return s.Passed ? GateResult.Pass($"{s.ScannedFiles} files scanned, {s.Findings.Count} warning(s)")
                    : GateResult.Fail(string.Join("; ", s.Findings.Where(f => f.Severity != "warn").Take(3).Select(f => $"{f.Rule} {f.File}:{f.Line}")));
            }),
        };
        return list.ToDictionary(g => g.Name);
    }
}

public sealed class GateFailedException(string gate, string reason) : Exception($"exit gate '{gate}' failed: {reason}")
{
    public string Gate { get; } = gate;
}

public sealed class NeedsHumanException(GateHuman human) : Exception("human input required")
{
    public GateHuman Human { get; } = human;
}

public sealed class CriticalPolicyException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
