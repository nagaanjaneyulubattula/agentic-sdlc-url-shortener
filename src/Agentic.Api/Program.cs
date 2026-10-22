using Agentic.Orchestrator;
using Agentic.Orchestrator.Agents;
using Microsoft.AspNetCore.Http.Json;

// Control plane for the agentic SDLC orchestrator: starts runs, exposes live state, audit, lineage, artifacts and the
// human approval queue to the Angular console (served from wwwroot).

if (args.Length > 0 && args[0] == "run") return await Headless.RunAsync(args.Skip(1).ToArray());

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    foreach (var c in Json.Options.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<RunManager>();

var app = builder.Build();
if (builder.Configuration["ASPNETCORE_URLS"] is null && builder.Configuration["urls"] is null) app.Urls.Add("http://localhost:5080");
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/scenarios", (RunManager rm) => rm.Scenarios.Select(s => new { s.Id, s.Title, s.Mode, s.Description, s.RequirementText }));

api.MapPost("/runs", (StartRunRequest req, RunManager rm) =>
{
    var id = rm.Start(req.ScenarioId, req.AutoApprove ?? false);
    return id is null ? Results.NotFound(new { error = $"Unknown scenario '{req.ScenarioId}'" }) : Results.Accepted($"/api/runs/{id}", new { runId = id });
});

api.MapGet("/runs", (RunManager rm) => rm.All().Select(r => new
{
    id = r.Engine.State.RunId, scenarioId = r.Engine.State.Scenario.Id, title = r.Engine.State.Scenario.Title,
    mode = r.Engine.State.Scenario.Mode, status = r.Engine.State.Status.ToString(), startedAt = r.Engine.State.StartedAt, endedAt = r.Engine.State.EndedAt,
}));

api.MapGet("/runs/{id}", (string id, RunManager rm) => rm.Find(id) is { } r ? Results.Ok(RunViews.Build(r.Engine)) : Results.NotFound());
api.MapGet("/runs/{id}/audit", (string id, RunManager rm) => rm.Find(id) is { } r ? Results.Ok(RunViews.AuditView(r.Engine)) : Results.NotFound());
api.MapGet("/runs/{id}/lineage", (string id, RunManager rm) => rm.Find(id) is { } r ? Results.Ok(RunViews.Lineage(r.Engine)) : Results.NotFound());
api.MapGet("/runs/{id}/artifacts", (string id, RunManager rm) => rm.Find(id) is { } r ? Results.Ok(RunViews.ArtifactIndex(r.Engine)) : Results.NotFound());
api.MapGet("/runs/{id}/artifacts/{name}", (string id, string name, RunManager rm) =>
    rm.Find(id)?.Engine.State.Artifacts.Latest(name) is { } a ? Results.Text(a.ToJsonString(Json.Pretty), "application/json") : Results.NotFound());
api.MapGet("/runs/{id}/summary", (string id, RunManager rm) =>
{
    var r = rm.Find(id);
    var path = r is null ? null : Path.Combine(r.Engine.State.RunDirectory, "ENGINEERING_SUMMARY.md");
    return path is not null && File.Exists(path) ? Results.Text(File.ReadAllText(path), "text/markdown") : Results.NotFound();
});

api.MapPost("/runs/{id}/approvals/{approvalId}", (string id, string approvalId, ApprovalBody body, RunManager rm) =>
{
    var r = rm.Find(id);
    if (r is null) return Results.NotFound();
    if (r.Queue is null) return Results.Conflict(new { error = "This run auto-approves gates (demo mode); no human queue." });
    var ok = r.Queue.Resolve(approvalId, new ApprovalDecision(body.Approved, string.IsNullOrWhiteSpace(body.Approver) ? "console-user" : body.Approver!, body.Comment ?? "", body.Answers));
    return ok ? Results.Ok(new { resolved = true }) : Results.NotFound(new { error = "No such pending approval" });
});

api.MapPost("/runs/{id}/stop", (string id, RunManager rm) =>
{
    var r = rm.Find(id);
    if (r is null) return Results.NotFound();
    r.Engine.RequestStop("Stopped by operator via console");
    return Results.Accepted();
});

api.MapPost("/runs/{id}/resume", (string id, RunManager rm) => rm.Resume(id) ? Results.Accepted() : Results.Conflict(new { error = "Run is not resumable" }));

app.MapFallbackToFile("index.html");
app.Run();
return 0;

public sealed record StartRunRequest(string ScenarioId, bool? AutoApprove);
public sealed record ApprovalBody(bool Approved, string? Comment, string? Approver, Dictionary<string, string>? Answers);

public sealed class ManagedRun(Engine engine, QueueApprovalProvider? queue)
{
    public Engine Engine { get; } = engine;
    public QueueApprovalProvider? Queue { get; } = queue;
    public Task? Task { get; set; }
}

public sealed class RunManager
{
    private readonly Dictionary<string, ManagedRun> _runs = new();
    private readonly object _lock = new();
    private readonly FeatureCatalog _catalog = FeatureCatalog.Load();
    private readonly string _runsRoot;

    public IReadOnlyList<ScenarioSpec> Scenarios { get; }

    public RunManager()
    {
        Scenarios = ScenarioLoader.LoadAll(_catalog.RepoRoot);
        _runsRoot = Path.Combine(_catalog.RepoRoot, "runs");
    }

    public string? Start(string scenarioId, bool autoApprove)
    {
        var template = Scenarios.FirstOrDefault(s => s.Id == scenarioId);
        if (template is null) return null;
        var scenario = ScenarioLoader.Clone(template);
        var queue = autoApprove ? null : new QueueApprovalProvider();
        IApprovalProvider provider = autoApprove ? new ScriptedApprovalProvider(scenario) : queue!;
        var engine = new Engine(scenario, _catalog, DefaultAgents.All(),
            new EngineOptions { RunsRoot = _runsRoot, TestRunner = new DotnetTestRunner(), Approvals = provider });
        var run = new ManagedRun(engine, queue);
        lock (_lock) _runs[engine.State.RunId] = run;
        run.Task = Task.Run(() => engine.RunAsync());
        return engine.State.RunId;
    }

    public bool Resume(string id)
    {
        var r = Find(id);
        if (r is null || r.Engine.State.Status is RunStatus.Running or RunStatus.AwaitingHuman or RunStatus.Completed) return false;
        r.Engine.State.Scenario.Faults.Clear(); // operator removed the cause of the injected fault before resuming
        r.Task = Task.Run(() => r.Engine.ResumeAsync());
        return true;
    }

    public ManagedRun? Find(string id) { lock (_lock) return _runs.GetValueOrDefault(id); }
    public IReadOnlyList<ManagedRun> All() { lock (_lock) return _runs.Values.OrderByDescending(r => r.Engine.State.StartedAt).ToList(); }
}
