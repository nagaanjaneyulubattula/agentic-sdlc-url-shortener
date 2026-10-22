using System.Text;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// Static, read-only analysis of the existing (brownfield) workspace. <c>mode=scan</c> builds a structural model of the code
/// (modules, routes, hooks, dependency graph, installed features, data flows, security findings, baseline test run);
/// <c>mode=impact</c> combines that scan with the requirements to compute the minimal change set and its risk.
/// Nothing about the baseline is hardcoded: everything is derived from the files in <see cref="AgentContext.Workspace"/>.
/// </summary>
public sealed class CodebaseAnalystAgent : IAgent
{
    public const string AgentName = "codebase-analyst";
    public string Name => AgentName;
    public AutonomyBoundary Autonomy { get; } = new(Array.Empty<string>(), CanRunTests: true);

    private const string DefaultRegistryPath = "src/UrlShortener.Api/Features/FeatureRegistry.cs";

    public async Task<AgentResult> RunAsync(AgentContext ctx, CancellationToken ct)
    {
        return ctx.Param("mode", "scan").ToLowerInvariant() == "impact" ? Impact(ctx) : await ScanAsync(ctx, ct);
    }

    // ═════════════════════════════════════════ scan ═════════════════════════════════════════

    private sealed record SourceFile(string Path, string Raw, string Code, string Bare);

    private static List<SourceFile> LoadSources(IWorkspace ws) =>
        ws.List("**/*")
            .Select(p => p.Replace('\\', '/'))
            .Where(p => p.StartsWith("src/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal) &&
                        !p.Contains("/bin/", StringComparison.Ordinal) && !p.Contains("/obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => { var raw = ws.ReadText(p); return new SourceFile(p, raw, Strip(raw, false), Strip(raw, true)); })
            .ToList();

    private async Task<AgentResult> ScanAsync(AgentContext ctx, CancellationToken ct)
    {
        var ws = ctx.Workspace;
        var cat = ctx.Catalog;
        var result = new AgentResult();
        var files = LoadSources(ws);

        // constants like Routes.Links = "/api/links" let us resolve MapPost(Routes.Links, ...)
        var consts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (Match m in Regex.Matches(f.Code, @"\bconst\s+string\s+(\w+)\s*=\s*""([^""\\]*)"""))
                consts.TryAdd(m.Groups[1].Value, m.Groups[2].Value);

        var modules = new List<ModuleInfo>();
        foreach (var f in files)
        {
            var usings = Regex.Matches(f.Bare, @"^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            modules.Add(new ModuleInfo(f.Path, usings, DeclaredTypes(f), RoutesOf(f, consts), HooksOf(f)));
        }

        // dependency graph: file -> other files whose declared type names occur as whole words
        var typeOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var m in modules) foreach (var t in m.Types) { if (!typeOwners.TryGetValue(t, out var l)) typeOwners[t] = l = new(); l.Add(m.Path); }
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (typeOwners.Count > 0)
        {
            var anyType = new Regex(@"\b(?:" + string.Join("|", typeOwners.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(Regex.Escape)) + @")\b", RegexOptions.Compiled);
            foreach (var f in files)
            {
                var deps = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Match m in anyType.Matches(f.Bare))
                    foreach (var owner in typeOwners[m.Value]) if (owner != f.Path) deps.Add(owner);
                graph[f.Path] = deps.ToList();
            }
        }
        else foreach (var f in files) graph[f.Path] = new();

        var routes = modules.SelectMany(m => m.Routes)
            .OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ThenBy(r => r.File, StringComparer.Ordinal).ToList();

        var installed = InstalledFeatures(ctx);
        var findings = FindingsOf(files);
        var flows = DataFlows(files, modules, routes, graph);

        TestReport? baseline = null;
        if (ws.Exists(cat.Core.TestProject))
        {
            try { baseline = await ctx.Tests.RunAsync(ws.Root, cat.Core.TestProject, null, TimeSpan.FromMinutes(3), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ctx.Log($"codebase-analyst: baseline test run failed to execute: {ex.Message}");
                baseline = new TestReport(0, 0, 0, 0, 0, -1, new() { "test runner error: " + ex.Message }, ex.Message);
            }
        }

        var report = new ImpactReport(installed, modules, graph, routes, new(), new(), new(), new(), flows, findings, baseline);
        result.Add("codebase-scan", report);
        result.Notes = $"{files.Count} source files, {routes.Count} routes, {installed.Count} installed features, {findings.Count} findings";
        return result;
    }

    private static List<string> DeclaredTypes(SourceFile f) =>
        Regex.Matches(f.Bare, @"\b(?:class|record(?:\s+(?:class|struct))?|interface|struct|enum)\s+(\w+)(?=\s*[:{(<;]|\s+where\b)")
            .Select(m => m.Groups[1].Value)
            .Where(n => !Keywords.Contains(n)).Distinct().ToList();

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "in", "is", "as", "new", "class", "struct", "record", "where", "when", "with", "this", "base" };

    private static readonly Regex TypeWithBases = new(
        @"\b(?:class|record(?:\s+(?:class|struct))?|struct)\s+(?<name>\w+)(?:\s*<[^>{]*>)?\s*(?:\([^)]*\))?\s*:\s*(?<bases>[^{;]+)");
    private static readonly Regex HookNames = new(@"\b(IShortenHook|IRedirectHook|IRequestGate|IFeature)\b");

    private static List<string> HooksOf(SourceFile f)
    {
        var hooks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in TypeWithBases.Matches(f.Bare))
            foreach (Match h in HookNames.Matches(m.Groups["bases"].Value)) hooks.Add(h.Value);
        return hooks.ToList();
    }

    /// <summary>Names of the types in the file that implement the given hook interface.</summary>
    private static IEnumerable<string> Implementors(SourceFile f, string hook) =>
        TypeWithBases.Matches(f.Bare).Where(m => Regex.IsMatch(m.Groups["bases"].Value, $@"\b{hook}\b")).Select(m => m.Groups["name"].Value);

    private static readonly Regex GroupDecl = new(@"(?<var>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*""(?<p>[^""]*)""");
    private static readonly Regex MapCall = new(
        @"(?:(?<recv>\w+)|\))\s*\.\s*Map(?<m>Get|Post|Put|Delete|Patch)\s*\(\s*(?<arg>[^,)]+)");

    /// <summary>Evaluates a route-template argument: string literals, interpolated strings and workspace constants joined with '+'.</summary>
    private static string? EvalPath(string arg, Dictionary<string, string> consts)
    {
        var terms = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in arg)
        {
            if (ch == '"') inQuotes = !inQuotes;
            if (ch == '+' && !inQuotes) { terms.Add(cur.ToString()); cur.Clear(); } else cur.Append(ch);
        }
        terms.Add(cur.ToString());

        var sb = new StringBuilder();
        foreach (var raw in terms)
        {
            var t = raw.Trim();
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') sb.Append(t[1..^1]);
            else if (t.Length >= 3 && t[0] == '$' && t[1] == '"' && t[^1] == '"')
            {
                var failed = false;
                sb.Append(Regex.Replace(t[2..^1], @"\{\{|\}\}|\{([\w.]+)\}", m =>
                {
                    if (m.Value == "{{") return "{";
                    if (m.Value == "}}") return "}";
                    var name = m.Groups[1].Value; name = name[(name.LastIndexOf('.') + 1)..];
                    if (consts.TryGetValue(name, out var v)) return v;
                    failed = true; return "";
                }));
                if (failed) return null;
            }
            else if (Regex.IsMatch(t, @"^[\w.]+$"))
            {
                var name = t[(t.LastIndexOf('.') + 1)..];
                if (!consts.TryGetValue(name, out var v)) return null;
                sb.Append(v);
            }
            else return null;
        }
        return sb.ToString();
    }

    private static List<RouteInfo> RoutesOf(SourceFile f, Dictionary<string, string> consts)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match g in GroupDecl.Matches(f.Code))
        {
            var prefix = g.Groups["p"].Value;
            if (groups.TryGetValue(g.Groups["parent"].Value, out var parentPrefix)) prefix = Join(parentPrefix, prefix);
            groups[g.Groups["var"].Value] = prefix;
        }
        var routes = new List<RouteInfo>();
        foreach (Match m in MapCall.Matches(f.Code))
        {
            var path = EvalPath(m.Groups["arg"].Value, consts);
            if (path is null) continue;
            var prefix = "";
            if (m.Groups["recv"].Success) groups.TryGetValue(m.Groups["recv"].Value, out prefix);
            else if (Regex.Match(f.Code[..m.Index], @"MapGroup\s*\(\s*""([^""]*)""\s*\)\s*$") is { Success: true } chained) prefix = chained.Groups[1].Value;
            routes.Add(new RouteInfo(m.Groups["m"].Value.ToUpperInvariant(), Join(prefix ?? "", path), f.Path));
        }
        return routes;
    }

    private static string Join(string prefix, string path)
    {
        var segs = new[] { prefix.Trim('/'), path.Trim('/') }.Where(s => s.Length > 0);
        return "/" + string.Join("/", segs);
    }

    private static List<string> InstalledFeatures(AgentContext ctx)
    {
        var path = string.IsNullOrEmpty(ctx.Catalog.Core.RegistryPath) ? DefaultRegistryPath : ctx.Catalog.Core.RegistryPath;
        if (!ctx.Workspace.Exists(path)) return new();
        var code = Strip(ctx.Workspace.ReadText(path), false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, @"new\s+(?:\w+\.)*(?<slice>\w+)\.(?<cls>\w+)Feature\s*\(\s*\)"))
        {
            if (m.Groups["slice"].Value != m.Groups["cls"].Value) continue;
            foreach (var f in ctx.Catalog.Features.Values.Where(f => f.Slice == m.Groups["slice"].Value)) ids.Add(f.Id);
        }
        return ids.OrderBy(id => ctx.Catalog.Features[id].Order).ThenBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static List<Finding> FindingsOf(List<SourceFile> files)
    {
        var list = new List<Finding>();
        foreach (var f in files)
        {
            foreach (var s in PolicyEngine.ScanFile(f.Path, f.Raw))
            {
                var rule = s.Rule;
                list.Add(new Finding("FIND-" + rule,
                    s.Severity switch { "critical" => "critical", "block" => "high", _ => "low" },
                    rule switch { "SEC-004" => "CWE-338", "SEC-003" => "CWE-918", "SEC-002" => "CWE-94", "SEC-001" => "CWE-798", _ when rule.StartsWith("COMP-") => "CWE-359", _ => null },
                    s.File, s.Line, s.Message, FixFor(rule)));
            }
            if (f.Path.EndsWith("Core/UrlValidator.cs", StringComparison.Ordinal))
            {
                if (!f.Raw.Contains("\"https\"", StringComparison.Ordinal) && !f.Raw.Contains("Uri.UriSchemeHttps", StringComparison.Ordinal))
                    list.Add(new Finding("FIND-URL-SCHEME", "high", "CWE-601", f.Path, 1,
                        "URL validation has no scheme allowlist: javascript:, data: and file: URLs would be accepted (open redirect / XSS vector).", "allow only http/https"));
                if (!f.Raw.Contains("169.254", StringComparison.Ordinal) && !f.Raw.Contains("IsLoopback", StringComparison.Ordinal) && !f.Raw.Contains("localhost", StringComparison.Ordinal))
                    list.Add(new Finding("FIND-URL-HOSTS", "high", "CWE-918", f.Path, 1,
                        "URL validation does not block internal destinations (loopback, private, link-local, cloud metadata) or embedded credentials (SSRF).",
                        "block loopback/private/link-local/metadata hosts and userinfo"));
            }
        }
        return list.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    private static string FixFor(string rule) => rule switch
    {
        "SEC-001" => "Remove the secret from source; load it from configuration or a secret store.",
        "SEC-002" => "Remove process execution / dynamic code loading from the service.",
        "SEC-003" => "Remove outbound network calls from the service.",
        "SEC-004" => "Use System.Security.Cryptography.RandomNumberGenerator (CSPRNG) instead of System.Random.",
        "COMP-001" => "Read the client address only in Core/ClientKey.cs and use the keyed hash elsewhere.",
        "COMP-002" => "Persist only the keyed client hash, never a raw IP address.",
        "QUAL-001" => "Handle or log the exception instead of swallowing it.",
        _ => "Review and fix according to the policy rule.",
    };

    private static List<string> DataFlows(List<SourceFile> files, List<ModuleInfo> modules, List<RouteInfo> routes, Dictionary<string, List<string>> graph)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var declared = modules.SelectMany(m => m.Types).ToHashSet(StringComparer.Ordinal);
        var flows = new List<string>();

        // ILinkStore methods (parsed from the interface declaration)
        var storeMethods = new List<string>();
        var storeFile = modules.FirstOrDefault(m => m.Types.Contains("ILinkStore"));
        if (storeFile is not null)
        {
            var bare = byPath[storeFile.Path].Bare;
            var decl = Regex.Match(bare, @"interface\s+ILinkStore\b[^{]*\{(?<body>.*?)\n\}", RegexOptions.Singleline);
            if (decl.Success)
                storeMethods = Regex.Matches(decl.Groups["body"].Value, @"^\s*[\w<>?\[\]]+\s+(\w+)\s*\(", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        }

        foreach (var route in routes)
        {
            var reach = Reachable(graph, route.File);
            string Name(string p) => System.IO.Path.GetFileName(p);
            var steps = new List<string> { $"{route.Method} {route.Path}", Name(route.File) };
            if (graph.TryGetValue(route.File, out var direct))
                steps.AddRange(direct.Where(d => d.Contains("/Features/", StringComparison.Ordinal)).Select(Name));
            // text of the reachable files that merely *use* the type (the file declaring it mentions it trivially)
            string UsersText(string type) => string.Join("\n", reach.Where(p => byPath.ContainsKey(p) &&
                !modules.Any(m => m.Path == p && m.Types.Contains(type))).Select(p => byPath[p].Bare));
            foreach (var t in new[] { "UrlValidator", "IShortenHook", "IRedirectHook", "CodeGenerator" })
                if (declared.Contains(t) && Regex.IsMatch(UsersText(t), $@"\b{t}\b")) steps.Add(t);
            if (declared.Contains("ILinkStore"))
            {
                var storeUsers = UsersText("ILinkStore");
                if (Regex.IsMatch(storeUsers, @"\bILinkStore\b"))
                {
                    var used = storeMethods.Where(m => Regex.IsMatch(storeUsers, $@"\.\s*{m}\s*\(")).ToList();
                    steps.Add(used.Count > 0 ? "ILinkStore." + string.Join("/", used) : "ILinkStore");
                }
            }
            flows.Add(string.Join(" → ", steps.Distinct()));
        }

        var gateSite = modules.FirstOrDefault(m => byPath[m.Path].Bare.Contains("IRequestGate", StringComparison.Ordinal) &&
                                                   Regex.IsMatch(byPath[m.Path].Bare, @"\.\s*BeforeAsync\s*\("));
        if (gateSite is not null)
        {
            var gates = modules.Where(m => m.Hooks.Contains("IRequestGate")).SelectMany(m => Implementors(byPath[m.Path], "IRequestGate")).ToList();
            flows.Add("every request → " + System.IO.Path.GetFileName(gateSite.Path) + " → IRequestGate.BeforeAsync" +
                      (gates.Count > 0 ? $" ({string.Join(", ", gates)})" : " (no gates installed)") + " → endpoint");
        }
        return flows;
    }

    /// <summary>
    /// Files on the request path starting at <paramref name="start"/>: the walk continues through feature-slice files but only
    /// *includes* shared core files (Core/Hooks.cs and friends would otherwise pull the whole codebase into every flow).
    /// </summary>
    private static HashSet<string> Reachable(Dictionary<string, List<string>> graph, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur != start && !cur.Contains("/Features/", StringComparison.Ordinal)) continue;
            if (!graph.TryGetValue(cur, out var deps)) continue;
            foreach (var d in deps) if (seen.Add(d)) queue.Enqueue(d);
        }
        return seen;
    }

    // ═════════════════════════════════════════ impact ═════════════════════════════════════════

    private AgentResult Impact(AgentContext ctx)
    {
        var cat = ctx.Catalog;
        var result = new AgentResult();
        var req = ctx.Read<RequirementsDoc>("requirements");
        var scan = ctx.ReadOrDefault<ImpactReport>("codebase-scan") ??
                   new ImpactReport(new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), null);

        var wanted = req.Features.Select(f => f.Id).Where(cat.Features.ContainsKey).ToHashSet(StringComparer.Ordinal);
        foreach (var patch in cat.Features.Values.Where(f => f.IsPatch))
            if (PlanningRules.PatchNeeded(patch, scan.Findings)) wanted.Add(patch.Id);
        var installed = scan.InstalledFeatures.ToHashSet(StringComparer.Ordinal);
        var toInstall = cat.Closure(wanted)
            .Where(id => cat.Get(id).IsPatch ? PlanningRules.PatchNeeded(cat.Get(id), scan.Findings) : !installed.Contains(id))
            .ToList();

        var newFiles = new SortedSet<string>(StringComparer.Ordinal);
        var modified = new SortedSet<string>(StringComparer.Ordinal);
        var apiChanges = new List<string>();
        var impacted = new Dictionary<string, Impacted>(StringComparer.Ordinal);
        void AddImpact(string path, string reason, string change, string risk)
        {
            if (impacted.TryGetValue(path, out var cur))
            {
                var keepChange = cur.Change == "modify" || change == "modify" ? "modify" : "extend";
                var keepRisk = new[] { "low", "medium", "high" }.ToList().IndexOf(cur.Risk) >= new[] { "low", "medium", "high" }.ToList().IndexOf(risk) ? cur.Risk : risk;
                impacted[path] = new Impacted(path, cur.Reason + "; " + reason, keepChange, keepRisk);
            }
            else impacted[path] = new Impacted(path, reason, change, risk);
        }

        var sliceAdded = false;
        foreach (var id in toInstall)
        {
            var def = cat.Get(id);
            if (def.IsPatch)
            {
                foreach (var f in def.PatchFiles)
                    if (!ctx.Workspace.Exists(f) && !def.Modifies.Contains(f)) newFiles.Add(f);
                foreach (var f in def.Modifies) modified.Add(f);
            }
            else
            {
                sliceAdded = true;
                foreach (var f in cat.FeatureFiles(id)) newFiles.Add(f);
            }
            foreach (var r in def.Routes) apiChanges.Add($"new endpoint {r.Method} {r.Path}");
            foreach (var rf in def.RequestFields) apiChanges.Add($"{rf.Route} gains optional field {rf.Field}");
            apiChanges.AddRange(PlanningRules.BehaviourChanges(id));
        }

        var registry = string.IsNullOrEmpty(cat.Core.RegistryPath) ? DefaultRegistryPath : cat.Core.RegistryPath;
        if (sliceAdded)
        {
            modified.Add(registry);
            AddImpact(registry, "re-generated to register new slices", "modify", "low");
            result.Decide($"Regenerate {registry}", "New slices must be registered; the file is generated, so the change is mechanical and low risk.",
                new[] { "Hand-edit the registry", "Use reflection-based discovery" }, "Generated file keeps registration deterministic at the cost of a regenerated diff.");
        }

        // patch-modified files: high risk
        foreach (var id in toInstall.Where(i => cat.Get(i).IsPatch))
        {
            var def = cat.Get(id);
            foreach (var file in def.Modifies)
            {
                var fixes = scan.Findings.Where(f => PlanningRules.IsBlocking(f) && PlanningRules.Addresses(def, f) &&
                                                     (f.File == file || f.Id.StartsWith("FIND-URL-", StringComparison.Ordinal) && file.EndsWith("UrlValidator.cs", StringComparison.Ordinal))).ToList();
                var through = RoutesThrough(scan, file);
                var reason = $"'{id}' patch rewrites this file" +
                             (fixes.Count > 0 ? $" to fix {string.Join(", ", fixes.Select(f => f.Id + (f.Cwe is null ? "" : $" ({f.Cwe})")).Distinct())}" : "") +
                             (through.Count > 0 ? $"; on the path of {string.Join(", ", through.Take(4))}" : "");
                AddImpact(file, reason, "modify", "high");
                result.Decide($"Modify {file}", reason + ". Change is confined to this file's validation/generation logic.",
                    new[] { "Wrap the file instead of editing it", "Leave the defect and accept the risk" },
                    "Editing security-sensitive code needs human approval, but wrapping would leave the vulnerable code reachable.");
            }
        }

        // hook invocation sites for every hook a new slice uses
        var sources = LoadSources(ctx.Workspace);
        foreach (var id in toInstall.Where(i => !cat.Get(i).IsPatch))
        {
            foreach (var hook in cat.Get(id).Hooks?.Uses ?? new())
            {
                foreach (var site in InvocationSites(sources, hook))
                    AddImpact(site, $"hook invocation site for '{hook}' (used by '{id}'); no code change, new implementation is additive", "extend", "medium");
            }
        }
        foreach (var i in impacted.Values.Where(i => i.Change == "extend").OrderBy(i => i.Path, StringComparer.Ordinal))
            result.Decide($"Do not modify {i.Path}", "It only invokes hooks through interfaces; the new slice implements the hook, so the call site stays untouched (change minimisation).",
                new[] { "Inline the new behaviour at the call site" }, "Keeps the diff small and the existing behaviour unchanged, at the cost of one more indirection.");

        var report = scan with
        {
            Impacted = impacted.Values.OrderBy(i => i.Path, StringComparer.Ordinal).ToList(),
            NewFiles = newFiles.ToList(),
            ModifiedFiles = modified.ToList(),
            ApiChanges = apiChanges.Distinct().ToList(),
        };
        result.Add("impact", report);
        result.Notes = $"toInstall=[{string.Join(", ", toInstall)}] new={newFiles.Count} modified={modified.Count}";
        return result;
    }

    private static List<string> RoutesThrough(ImpactReport scan, string file) =>
        scan.Routes.Where(r => Reachable(scan.DependencyGraph, r.File).Contains(file))
            .Select(r => $"{r.Method} {r.Path}").Distinct().ToList();

    private static IEnumerable<string> InvocationSites(List<SourceFile> sources, string hook)
    {
        (string call, string? context) = hook switch
        {
            "onShorten" => (@"\.\s*OnShortenAsync\s*\(", null),
            "resolveCode" => (@"\.\s*ResolveCodeAsync\s*\(", null),
            "afterShorten" => (@"\.\s*AfterShortenAsync\s*\(", null),
            "beforeRedirect" => (@"\.\s*BeforeAsync\s*\(", @"\b(IRedirectHook|RedirectContext)\b"),
            "afterRedirect" => (@"\.\s*AfterAsync\s*\(", @"\b(IRedirectHook|RedirectContext)\b"),
            "beforeRequest" => (@"\.\s*BeforeAsync\s*\(", @"\bIRequestGate\b"),
            _ => ("", null),
        };
        if (call.Length == 0) yield break;
        foreach (var s in sources)
            if (Regex.IsMatch(s.Code, call) && (context is null || Regex.IsMatch(s.Bare, context))) yield return s.Path;
    }

    // ═════════════════════════════════════════ source stripping ═════════════════════════════════════════

    /// <summary>Blanks comments (and optionally string-literal contents) while preserving offsets and line breaks.</summary>
    internal static string Strip(string src, bool blankStrings)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0, n = src.Length;
        void Blank(char c) => sb.Append(c == '\n' || c == '\r' ? c : ' ');
        while (i < n)
        {
            var c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') { Blank(src[i]); i++; }
            }
            else if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                sb.Append("  "); i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/')) { Blank(src[i]); i++; }
                if (i < n) { sb.Append("  "); i += 2; }
            }
            else if (c == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')
            {
                // raw string literal """ ... """
                var start = i; i += 3;
                while (i < n && !(src[i] == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')) i++;
                i = Math.Min(n, i + 3);
                for (var k = start; k < i; k++) { if (blankStrings && src[k] != '"') Blank(src[k]); else sb.Append(src[k]); }
            }
            else if (c == '"' || (c == '@' && i + 1 < n && src[i + 1] == '"') || (c == '$' && i + 1 < n && src[i + 1] == '"') ||
                     ((c == '$' || c == '@') && i + 2 < n && (src[i + 1] == '$' || src[i + 1] == '@') && src[i + 2] == '"'))
            {
                var verbatim = false;
                while (src[i] != '"') { if (src[i] == '@') verbatim = true; sb.Append(src[i]); i++; }
                sb.Append('"'); i++;
                while (i < n)
                {
                    if (verbatim && src[i] == '"' && i + 1 < n && src[i + 1] == '"') { Put("\"\""); i += 2; continue; }
                    if (!verbatim && src[i] == '\\' && i + 1 < n) { Put(src.Substring(i, 2)); i += 2; continue; }
                    if (src[i] == '"') break;
                    if (!verbatim && src[i] == '\n') break; // unterminated: stop at line end
                    Put(src[i].ToString()); i++;
                }
                if (i < n && src[i] == '"') { sb.Append('"'); i++; }

                void Put(string s) { foreach (var ch in s) { if (blankStrings) Blank(ch); else sb.Append(ch); } }
            }
            else if (c == '\'')
            {
                sb.Append(c); i++;
                while (i < n && src[i] != '\'' && src[i] != '\n') { if (src[i] == '\\' && i + 1 < n) { Blank(src[i]); Blank(src[i + 1]); i += 2; } else { Blank(src[i]); i++; } }
                if (i < n && src[i] == '\'') { sb.Append('\''); i++; }
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }
}
