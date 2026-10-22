using System.Text.RegularExpressions;

namespace Agentic.Orchestrator;

public sealed record PolicyRule(string Id, string Category, string Severity, string Description, string PathPattern, Regex Pattern, Regex? ExemptPath = null);

/// <summary>
/// Static policy guardrails for security, compliance and change control that every generated change must satisfy.
/// Severity: <c>critical</c> = immediate safe-stop (no retry), <c>block</c> = fails the node's exit gate (retry/rollback), <c>warn</c> = recorded only.
/// Change-control rules that need run context (blast radius, sensitive paths needing approval) live in the engine's <see cref="ChangeControl"/>.
/// </summary>
public static class PolicyEngine
{
    private static readonly RegexOptions Opt = RegexOptions.Compiled | RegexOptions.Multiline;

    public static readonly IReadOnlyList<PolicyRule> Rules = new List<PolicyRule>
    {
        new("SEC-001", "security", "critical", "No secrets or private keys in generated code",
            @"\.(cs|json|csproj|md)$", new Regex(@"AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|(?i:(password|secret|apikey|api_key|connectionstring))\s*[:=]\s*""[^""\s]{8,}""", Opt)),
        new("SEC-002", "security", "block", "No process execution / dynamic code loading / insecure deserialisation in the service",
            @"^src/UrlShortener\.Api/.*\.cs$", new Regex(@"Process\.Start|System\.Diagnostics\.Process|Assembly\.Load|BinaryFormatter|CSharpScript|Activator\.CreateInstance\(\s*Type\.GetType", Opt)),
        new("SEC-003", "security", "block", "The service must not make outbound HTTP/socket calls (SSRF / data-exfiltration surface)",
            @"^src/UrlShortener\.Api/.*\.cs$", new Regex(@"\bHttpClient\b|\bWebClient\b|\bTcpClient\b|\bSocket\b|HttpWebRequest", Opt)),
        new("SEC-004", "security", "block", "Cryptographically secure randomness only (no System.Random) in the service",
            @"^src/UrlShortener\.Api/.*\.cs$", new Regex(@"new\s+Random\s*\(|\bRandom\.Shared\b", Opt)),
        new("COMP-001", "compliance", "block", "Raw client IP may only be read in Core/ClientKey.cs (privacy by design)",
            @"^src/UrlShortener\.Api/.*\.cs$", new Regex(@"RemoteIpAddress|X-Forwarded-For", Opt), new Regex(@"Core/ClientKey\.cs$")),
        new("COMP-002", "compliance", "block", "Analytics/feature code must not persist raw IP addresses",
            @"^src/UrlShortener\.Api/Features/.*\.cs$", new Regex(@"\b(string|var|IPAddress)\??\s+(ip|ipAddress|clientIp|remoteIp)\b", Opt)),
        new("QUAL-001", "quality", "warn", "Empty catch block swallows errors silently",
            @"^src/UrlShortener\.Api/.*\.cs$", new Regex(@"catch\s*(\([^)]*\))?\s*\{\s*\}", Opt)),
    };

    public static List<SecurityFinding> ScanFile(string path, string content)
    {
        var findings = new List<SecurityFinding>();
        var norm = path.Replace('\\', '/');
        foreach (var rule in Rules)
        {
            if (!Regex.IsMatch(norm, rule.PathPattern)) continue;
            if (rule.ExemptPath is not null && rule.ExemptPath.IsMatch(norm)) continue;
            foreach (Match m in rule.Pattern.Matches(content))
            {
                var line = content.AsSpan(0, m.Index).Count('\n') + 1;
                findings.Add(new SecurityFinding(rule.Id, rule.Severity, norm, line, $"{rule.Description} (matched '{Trim(m.Value)}')"));
            }
        }
        return findings;
    }

    public static List<SecurityFinding> ScanFiles(IEnumerable<string> paths, Func<string, string> read)
    {
        var all = new List<SecurityFinding>();
        foreach (var p in paths) all.AddRange(ScanFile(p, read(p)));
        return all;
    }

    public static SecurityReport ScanWorkspace(IWorkspace ws)
    {
        var files = ws.List("**/*").Where(p => p.EndsWith(".cs") || p.EndsWith(".json") || p.EndsWith(".csproj") || p.EndsWith(".md")).ToList();
        var findings = ScanFiles(files, ws.ReadText);
        return new SecurityReport(files.Count, findings, findings.All(f => f.Severity == "warn"));
    }

    private static string Trim(string s) => s.Length <= 40 ? s.Replace("\n", " ") : s[..40].Replace("\n", " ") + "…";
}

/// <summary>Change-control guardrails evaluated by the engine before/after a node commits its changes.</summary>
public static class ChangeControl
{
    public const int MaxFilesChangedPerRun = 120;

    /// <summary>Paths whose modification is security-sensitive and always needs a human approval.</summary>
    public static bool IsSensitivePath(string path) =>
        Regex.IsMatch(path.Replace('\\', '/'), @"(UrlValidator|CodeGenerator|ClientKey|Program)\.cs$|/Features/(RateLimit|Analytics)/");

    public static string Describe(IEnumerable<FileChange> changes)
    {
        var list = changes.ToList();
        var lines = list.Select(c => $"  {c.Op,-8} {c.Path} ({c.Bytes} bytes)");
        return $"{list.Count} file(s): {list.Count(c => c.Op == "create")} created, {list.Count(c => c.Op == "modify")} modified\n" + string.Join("\n", lines);
    }
}
