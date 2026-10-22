namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Assembles the generated workspace from the vetted component library (the repo's own product source).
/// Modes (param "mode"): scaffold | slice | patch | integrate. Param "feature" selects the catalog feature for slice/patch.
/// Deterministic: the same inputs always produce the same files.
/// </summary>
public sealed class ImplementerAgent : IAgent
{
    public const string AgentName = "implementer";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => ImplementerWork.Boundary;

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return ImplementerWork.Run(ctx, cleanRoom: false);
    }
}

/// <summary>
/// Fallback strategy for the implementer: for slice installs it first wipes every workspace file under the slice's src and test folders
/// (discarding any partial / corrupted state) and reinstalls the slice from the library. Other modes behave like <see cref="ImplementerAgent"/>.
/// </summary>
public sealed class ImplementerCleanRoomAgent : IAgent
{
    public const string AgentName = "implementer-clean-room";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy => ImplementerWork.Boundary;

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return ImplementerWork.Run(ctx, cleanRoom: true);
    }
}

/// <summary>Shared implementation of both implementer agents (kept in one place so the fallback cannot drift from the primary).</summary>
internal static class ImplementerWork
{
    public static readonly AutonomyBoundary Boundary = new(new[] { "src/**", "tests/**", ".gitignore" });

    public const string GitIgnoreContent = "bin/\nobj/\n";

    public static AgentResult Run(AgentContext ctx, bool cleanRoom)
    {
        var mode = ctx.Param("mode");
        return mode switch
        {
            "scaffold" => Scaffold(ctx),
            "slice" => InstallFeature(ctx, "slice", cleanRoom),
            "patch" => InstallFeature(ctx, "patch", cleanRoom: false),
            "integrate" => Integrate(ctx),
            _ => throw new InvalidOperationException($"{(cleanRoom ? ImplementerCleanRoomAgent.AgentName : ImplementerAgent.AgentName)}: unknown or missing param 'mode' ('{mode}'); expected scaffold | slice | patch | integrate."),
        };
    }

    // ───────────── scaffold ─────────────

    private static AgentResult Scaffold(AgentContext ctx)
    {
        var ws = ctx.Workspace;
        var files = new List<FileChange>();
        foreach (var rel in ctx.Catalog.CoreFiles(baseline: false))
            files.Add(ExecutionSupport.CopyTracked(ws, ctx.Catalog.ResolveSource(rel), rel, ws.Exists(rel) ? ws.ReadText(rel) : null));
        files.Add(ExecutionSupport.WriteTracked(ws, ".gitignore", GitIgnoreContent));
        if (files.Count == 1) throw new InvalidOperationException("scaffold: the component library contains no core files (check library/catalog.json 'core').");

        ctx.Log($"scaffold: {files.Count} file(s) ({Count(files, "create")} created, {Count(files, "modify")} modified, {Count(files, "unchanged")} unchanged)");
        return new AgentResult()
            .Add("scaffold", new ImplReport("core", files, "assemble-from-library"))
            .Decide("Scaffold the core from the vetted library",
                $"Copied {files.Count} core file(s) (API host, Core primitives, test support, project files) verbatim from the component library into the empty workspace, plus a .gitignore for bin/ and obj/.",
                new[] { "Generate the project skeleton from templates at run time", "Ask the user to provide a starter repository" },
                "Library reuse is deterministic and already compiled/tested in this repository, but the skeleton can only be as flexible as the library's core.");
    }

    // ───────────── slice / patch ─────────────

    private static AgentResult InstallFeature(AgentContext ctx, string mode, bool cleanRoom)
    {
        var featureId = ctx.Param("feature");
        if (string.IsNullOrWhiteSpace(featureId))
            throw new InvalidOperationException($"implementer ({mode}): missing param 'feature'.");
        var cat = ctx.Catalog;
        var ws = ctx.Workspace;
        var def = cat.Get(featureId);

        // Requirements must already be installed (the planner orders nodes by dependency; this verifies it at execution time).
        var missing = def.Requires
            .Select(cat.Get)
            .Where(r => ExecutionSupport.IsSlice(r) && !ExecutionSupport.HasSliceFiles(ws, cat, r))
            .Select(r => $"'{r.Id}' (expected files under {ExecutionSupport.SliceSrcDir(cat, r)}/)")
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Cannot install '{featureId}': required feature(s) not installed in the workspace: {string.Join(", ", missing)}.");

        var libraryFiles = cat.FeatureFiles(featureId);
        if (libraryFiles.Count == 0)
            throw new InvalidOperationException($"Cannot install '{featureId}': the component library has no source files for it.");

        // Snapshot of the pre-node state of every file we may touch, so ops are reported relative to it (also for clean-room reinstalls).
        var prior = new Dictionary<string, string>(StringComparer.Ordinal);
        var stale = new List<FileChange>();
        if (cleanRoom && ExecutionSupport.IsSlice(def))
        {
            var dirs = new[] { ExecutionSupport.SliceSrcDir(cat, def), ExecutionSupport.SliceTestDir(cat, def) };
            foreach (var existing in dirs.SelectMany(d => ExecutionSupport.FilesUnder(ws, d)).Distinct(StringComparer.Ordinal).ToList())
            {
                prior[existing] = ws.ReadText(existing);
                var sha = ws.Sha256(existing);
                ws.Remove(existing);
                if (!libraryFiles.Contains(existing, StringComparer.Ordinal))
                    stale.Add(new FileChange(existing, "delete", sha, System.Text.Encoding.UTF8.GetByteCount(prior[existing])));
            }
        }
        else
        {
            foreach (var rel in libraryFiles) if (ws.Exists(rel)) prior[rel] = ws.ReadText(rel);
        }

        var files = new List<FileChange>();
        foreach (var rel in libraryFiles)
            files.Add(ExecutionSupport.CopyTracked(ws, cat.ResolveSource(rel), rel, prior.GetValueOrDefault(rel)));
        files.AddRange(stale);

        var strategy = cleanRoom && ExecutionSupport.IsSlice(def) ? "clean-room-reinstall" : mode == "patch" ? "apply-patch" : "assemble-from-library";
        ctx.Log($"{mode} '{featureId}' via {strategy}: {files.Count} file(s) ({Count(files, "create")} created, {Count(files, "modify")} modified, {Count(files, "unchanged")} unchanged, {Count(files, "delete")} deleted)");

        var result = new AgentResult().Add($"impl:{featureId}", new ImplReport(featureId, files, strategy));
        var requires = def.Requires.Count == 0 ? "no prerequisites" : $"prerequisites present: {string.Join(", ", def.Requires)}";
        return strategy switch
        {
            "clean-room-reinstall" => result.Decide($"Clean-room reinstall of '{featureId}'",
                $"Removed {prior.Count} existing file(s) under the slice's src/test folders ({stale.Count} not part of the library) and reinstalled {libraryFiles.Count} file(s) from the library; {requires}.",
                new[] { "Overwrite files in place (keeps stale files from the failed attempt)", "Abort the run" },
                "Discards any local edits inside the slice folders; acceptable because slices are library-owned."),
            "apply-patch" => result.Decide($"Apply patch '{featureId}'",
                $"Applied {libraryFiles.Count} library file(s) over the existing workspace ({Count(files, "modify")} modified, {Count(files, "create")} created, {Count(files, "unchanged")} unchanged).",
                new[] { "Rewrite the affected files by hand", "Leave the defect and document it as a known issue" },
                "Whole-file replacement from the vetted library is simple and reviewable, but bypasses fine-grained merging with local edits."),
            _ => result.Decide($"Install slice '{featureId}' from the library",
                $"Copied {libraryFiles.Count} file(s) (slice sources and slice tests) from the component library; {requires}.",
                new[] { "Generate the slice code from scratch", "Install the whole library regardless of the requested features" },
                "Only requested features (and their requirements) enter the workspace, keeping the blast radius small; slices cannot be customised beyond what the library offers."),
        };
    }

    // ───────────── integrate ─────────────

    private static AgentResult Integrate(AgentContext ctx)
    {
        var cat = ctx.Catalog;
        var ws = ctx.Workspace;
        var installed = cat.Features.Values
            .Where(f => ExecutionSupport.IsSlice(f) && ExecutionSupport.HasSliceFiles(ws, cat, f))
            .Select(f => f.Id).ToList();
        if (installed.Count == 0)
            throw new InvalidOperationException("integrate: no feature slices are installed in the workspace; nothing to register.");

        // A registry that references a slice whose requirement is absent would not compile – fail early with a precise message.
        var unmet = installed.SelectMany(id => cat.Get(id).Requires.Select(r => (Feature: id, Requirement: cat.Get(r)))
                .Where(x => ExecutionSupport.IsSlice(x.Requirement) && !installed.Contains(x.Requirement.Id))
                .Select(x => $"'{x.Feature}' requires '{x.Requirement.Id}'")).ToList();
        if (unmet.Count > 0)
            throw new InvalidOperationException($"integrate: inconsistent installation – {string.Join("; ", unmet)} but the required slice is not present in the workspace.");

        var withHardening = cat.Features.ContainsKey("hardening") && ws.Exists(cat.Core.HardeningTestFile);
        var ordered = cat.Closure(withHardening ? installed.Append("hardening") : installed);
        var registry = ExecutionSupport.WriteTracked(ws, cat.Core.RegistryPath, cat.RenderRegistry(installed));

        ctx.Log($"integrate: registered {installed.Count} slice(s): {string.Join(", ", ordered)}");
        return new AgentResult()
            .Add("integration", new IntegrationReport(ordered, cat.Core.RegistryPath, new List<FileChange> { registry }))
            .Decide("Generate the feature registry from the installed slices",
                $"Detected {installed.Count} installed slice(s) from the workspace ({string.Join(", ", ordered.Where(i => i != "hardening"))}) and rendered {cat.Core.RegistryPath} in dependency order{(withHardening ? "; the hardening patch is applied (hardening tests present)" : "")}.",
                new[] { "Reflection-based discovery at start-up", "Hand-maintained registry file" },
                "A generated, deterministic registry keeps start-up cheap and reviewable, but must be regenerated whenever the slice set changes.");
    }

    private static int Count(IEnumerable<FileChange> files, string op) => files.Count(f => f.Op == op);
}
