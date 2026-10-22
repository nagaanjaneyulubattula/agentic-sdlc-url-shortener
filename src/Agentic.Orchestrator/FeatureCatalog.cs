using System.Text;
using System.Text.Json;

namespace Agentic.Orchestrator;

public sealed record AcceptanceCriterion(string Id, string Text);
public sealed record RouteDef(string Method, string Path, string Summary, Dictionary<string, string>? Request, Dictionary<string, string>? Responses);
public sealed record RiskDef(string Id, string Scenario, string Likelihood, string Impact, string Mitigation, List<string> Validation);
public sealed record HookDef(List<string>? Provides, List<string>? Uses);
public sealed record RequestFieldDef(string Route, string Field, string Type);

/// <summary>A catalog entry. Kind: core | feature | patch. Slices (core/feature) map to Features/&lt;Slice&gt; folders; patches list explicit files.</summary>
public sealed class FeatureDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "feature";
    public string? Slice { get; set; }
    public List<string> Requires { get; set; } = new();
    public int Order { get; set; }
    public bool Sensitive { get; set; }
    public string? SensitiveReason { get; set; }
    public List<string> Keywords { get; set; } = new();
    public HookDef? Hooks { get; set; }
    public List<RouteDef> Routes { get; set; } = new();
    public List<RequestFieldDef> RequestFields { get; set; } = new();
    public List<AcceptanceCriterion> Acceptance { get; set; } = new();
    public List<RiskDef> Risks { get; set; } = new();
    public List<string> PatchFiles { get; set; } = new();
    public List<string> Modifies { get; set; } = new();
    public bool IsPatch => Kind == "patch";
}

public sealed class CoreDef
{
    public List<string> Dirs { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public string SliceSrcDir { get; set; } = "";
    public string SliceTestDir { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public string TestProject { get; set; } = "";
    public string HardeningTestFile { get; set; } = "";
}

public sealed class SeededDefect
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Cwe { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class BaselineDef
{
    public string OverrideRoot { get; set; } = "library/baseline";
    public List<string> ExcludeCoreFiles { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public List<SeededDefect> SeededDefects { get; set; } = new();
}

/// <summary>
/// The "vetted component library": the repository's own product source, organised in feature slices and described by library/catalog.json.
/// The implementer agent assembles generated workspaces from it; the architect/analyst reason over it.
/// </summary>
public sealed class FeatureCatalog
{
    public string RepoRoot { get; }
    public CoreDef Core { get; private set; } = new();
    public BaselineDef Baseline { get; private set; } = new();
    public Dictionary<string, FeatureDef> Features { get; private set; } = new();
    public Dictionary<string, JsonElement> NfrDefaults { get; private set; } = new();

    private FeatureCatalog(string repoRoot) => RepoRoot = repoRoot;

    public static string FindRepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "library", "catalog.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root (library/catalog.json) from " + (start ?? AppContext.BaseDirectory));
    }

    public static FeatureCatalog Load(string? repoRoot = null)
    {
        repoRoot ??= FindRepoRoot();
        var cat = new FeatureCatalog(repoRoot);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "library", "catalog.json")));
        var root = doc.RootElement;
        cat.Core = root.GetProperty("core").Deserialize<CoreDef>(Json.Options)!;
        cat.Baseline = root.GetProperty("baseline").Deserialize<BaselineDef>(Json.Options)!;
        cat.Features = root.GetProperty("features").Deserialize<Dictionary<string, FeatureDef>>(Json.Options)!;
        foreach (var (id, f) in cat.Features) f.Id = id;
        if (root.TryGetProperty("nfrDefaults", out var n)) cat.NfrDefaults = n.Deserialize<Dictionary<string, JsonElement>>(Json.Options)!;
        return cat;
    }

    public FeatureDef Get(string id) =>
        Features.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unknown feature '{id}'.");

    /// <summary>Transitive closure over Requires, ordered so every feature follows its requirements (ties by Order, then Id). Patches are kept.</summary>
    public List<string> Closure(IEnumerable<string> ids)
    {
        var all = new HashSet<string>();
        void Visit(string id) { if (all.Add(id)) foreach (var r in Get(id).Requires) Visit(r); }
        foreach (var id in ids) Visit(id);
        var result = new List<string>();
        var remaining = all.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining
                .Where(id => Get(id).Requires.All(result.Contains))
                .OrderBy(id => Get(id).IsPatch ? -1 : 0).ThenBy(id => Get(id).Order).ThenBy(id => id, StringComparer.Ordinal)
                .First();
            result.Add(next);
            remaining.Remove(next);
        }
        return result;
    }

    /// <summary>Repo-relative source files that make up a feature (slice src+test folders, or the patch's explicit file list).</summary>
    public List<string> FeatureFiles(string featureId)
    {
        var f = Get(featureId);
        if (f.IsPatch) return f.PatchFiles.ToList();
        var list = new List<string>();
        foreach (var dirTemplate in new[] { Core.SliceSrcDir, Core.SliceTestDir })
        {
            var rel = dirTemplate.Replace("{Slice}", f.Slice);
            list.AddRange(EnumerateDir(rel));
        }
        return list;
    }

    /// <summary>Repo-relative files making up the core (greenfield) or the baseline's core (brownfield seed).</summary>
    public List<string> CoreFiles(bool baseline)
    {
        var list = new List<string>();
        foreach (var d in Core.Dirs) list.AddRange(EnumerateDir(d));
        foreach (var file in Core.Files) if (File.Exists(Path.Combine(RepoRoot, file))) list.Add(file);
        if (baseline) list.RemoveAll(p => Baseline.ExcludeCoreFiles.Contains(p));
        return list.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Absolute path of the library source for a repo-relative path (baseline override wins when baseline=true).</summary>
    public string ResolveSource(string relPath, bool baseline = false)
    {
        if (baseline)
        {
            var o = Path.Combine(RepoRoot, Baseline.OverrideRoot, relPath);
            if (File.Exists(o)) return o;
        }
        return Path.Combine(RepoRoot, relPath);
    }

    /// <summary>Generates Features/FeatureRegistry.cs for the given feature ids (patches are ignored). Deterministic.</summary>
    public string RenderRegistry(IEnumerable<string> featureIds)
    {
        var slices = Closure(featureIds).Select(Get).Where(f => !f.IsPatch && f.Slice is not null).OrderBy(f => f.Order).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated by Agentic.Orchestrator implementer agent>");
        sb.AppendLine("namespace UrlShortener.Api.Features;");
        sb.AppendLine();
        sb.AppendLine("public static class FeatureRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static IReadOnlyList<UrlShortener.Api.Core.IFeature> All { get; } = new UrlShortener.Api.Core.IFeature[]");
        sb.AppendLine("    {");
        foreach (var f in slices) sb.AppendLine($"        new UrlShortener.Api.Features.{f.Slice}.{f.Slice}Feature(),");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public int NfrInt(string key, int fallback) =>
        NfrDefaults.TryGetValue(key, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    private IEnumerable<string> EnumerateDir(string relDir)
    {
        var abs = Path.Combine(RepoRoot, relDir);
        if (!Directory.Exists(abs)) yield break;
        foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            yield return rel;
        }
    }
}
