using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator;

public sealed class WorkspaceConflictException(string message) : Exception(message);

/// <summary>
/// Owns the generated workspace directory. Every agent gets a per-node capability (<see cref="NodeWorkspace"/>) that
/// (1) is confined to the workspace root, (2) enforces the agent's write boundary (autonomy), (3) journals every write
/// so a node can be rolled back (failed attempt / rejected approval) or reverted later (dynamic re-planning), and
/// (4) detects concurrent writes to the same file by parallel nodes.
/// </summary>
public sealed class WorkspaceManager
{
    private sealed record JournalEntry(string Path, bool ExistedBefore, byte[]? Previous);
    private sealed class Journal
    {
        public List<JournalEntry> Entries { get; } = new();
        public Dictionary<string, FileChange> Net { get; } = new();
        public bool Committed { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Journal> _journals = new();
    private readonly Dictionary<string, string> _openOwners = new(); // path -> node id with uncommitted write

    public string Root { get; }

    public WorkspaceManager(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public NodeWorkspace ForNode(string nodeId, AutonomyBoundary boundary)
    {
        lock (_lock) { _journals[nodeId] = new Journal(); } // fresh journal per attempt
        return new NodeWorkspace(this, nodeId, boundary);
    }

    /// <summary>Undo every write of the node (uncommitted or committed). Returns how many files were restored/removed.</summary>
    public int Rollback(string nodeId)
    {
        lock (_lock)
        {
            if (!_journals.TryGetValue(nodeId, out var j)) return 0;
            var restored = 0;
            for (var i = j.Entries.Count - 1; i >= 0; i--)
            {
                var e = j.Entries[i];
                var abs = Abs(e.Path);
                if (e.ExistedBefore) { Directory.CreateDirectory(Path.GetDirectoryName(abs)!); File.WriteAllBytes(abs, e.Previous!); }
                else if (File.Exists(abs)) File.Delete(abs);
                restored++;
            }
            foreach (var p in j.Net.Keys) if (_openOwners.TryGetValue(p, out var o) && o == nodeId) _openOwners.Remove(p);
            _journals.Remove(nodeId);
            return restored;
        }
    }

    public void Commit(string nodeId)
    {
        lock (_lock)
        {
            if (!_journals.TryGetValue(nodeId, out var j)) return;
            j.Committed = true;
            foreach (var p in j.Net.Keys) if (_openOwners.TryGetValue(p, out var o) && o == nodeId) _openOwners.Remove(p);
        }
    }

    public IReadOnlyList<FileChange> Changes(string nodeId)
    {
        lock (_lock) return _journals.TryGetValue(nodeId, out var j) ? j.Net.Values.ToList() : new();
    }

    // ── internals used by NodeWorkspace ──
    internal string Abs(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.Contains(':'))
            throw new AutonomyViolationException($"Path '{rel}' must be workspace-relative.");
        var full = Path.GetFullPath(Path.Combine(Root, rel));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AutonomyViolationException($"Path '{rel}' escapes the workspace sandbox.");
        return full;
    }

    internal FileChange Write(string nodeId, AutonomyBoundary boundary, string rel, byte[] content)
    {
        rel = Normalize(rel);
        if (!Glob.MatchesAny(boundary.WriteGlobs, rel))
            throw new AutonomyViolationException($"Write to '{rel}' is outside the autonomy boundary [{string.Join(", ", boundary.WriteGlobs)}].");
        var abs = Abs(rel);
        lock (_lock)
        {
            if (_openOwners.TryGetValue(rel, out var owner) && owner != nodeId)
                throw new WorkspaceConflictException($"'{rel}' is being modified by node '{owner}' (concurrent write conflict).");
            var j = _journals[nodeId];
            var existed = File.Exists(abs);
            var previous = existed ? File.ReadAllBytes(abs) : null;
            if (existed && previous!.AsSpan().SequenceEqual(content))
                return new FileChange(rel, "unchanged", Sha(content), content.Length);
            if (!j.Net.ContainsKey(rel) || !j.Entries.Any(e => e.Path == rel))
                j.Entries.Add(new JournalEntry(rel, existed, previous));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllBytes(abs, content);
            var firstExisted = j.Entries.First(e => e.Path == rel).ExistedBefore;
            var change = new FileChange(rel, firstExisted ? "modify" : "create", Sha(content), content.Length);
            j.Net[rel] = change;
            _openOwners[rel] = nodeId;
            return change;
        }
    }

    internal void RemoveFile(string nodeId, AutonomyBoundary boundary, string rel)
    {
        rel = Normalize(rel);
        if (!Glob.MatchesAny(boundary.WriteGlobs, rel))
            throw new AutonomyViolationException($"Delete of '{rel}' is outside the autonomy boundary.");
        var abs = Abs(rel);
        lock (_lock)
        {
            if (!File.Exists(abs)) return;
            if (_openOwners.TryGetValue(rel, out var owner) && owner != nodeId)
                throw new WorkspaceConflictException($"'{rel}' is being modified by node '{owner}'.");
            var j = _journals[nodeId];
            if (!j.Entries.Any(e => e.Path == rel)) j.Entries.Add(new JournalEntry(rel, true, File.ReadAllBytes(abs)));
            File.Delete(abs);
            j.Net[rel] = new FileChange(rel, "delete", "", 0);
            _openOwners[rel] = nodeId;
        }
    }

    internal static string Normalize(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.StartsWith('/') || rel.StartsWith('\\') || rel.Contains(':'))
            throw new AutonomyViolationException($"Path '{rel}' must be workspace-relative.");
        return rel.Replace('\\', '/');
    }
    internal static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
}

/// <summary>Per-node, boundary-enforcing view of the workspace.</summary>
public sealed class NodeWorkspace : IWorkspace
{
    private readonly WorkspaceManager _m;
    private readonly string _nodeId;
    private readonly AutonomyBoundary _boundary;

    internal NodeWorkspace(WorkspaceManager m, string nodeId, AutonomyBoundary boundary) { _m = m; _nodeId = nodeId; _boundary = boundary; }

    public string Root => _m.Root;
    public bool Exists(string rel) => File.Exists(_m.Abs(WorkspaceManager.Normalize(rel)));
    public string ReadText(string rel) => File.ReadAllText(_m.Abs(WorkspaceManager.Normalize(rel)), Encoding.UTF8);
    public string Sha256(string rel) => WorkspaceManager.Sha(File.ReadAllBytes(_m.Abs(WorkspaceManager.Normalize(rel))));

    public IReadOnlyList<string> List(string glob = "**/*") =>
        Directory.EnumerateFiles(_m.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_m.Root, f).Replace('\\', '/'))
            .Where(p => !Glob.IsExcludedDir(p) && Glob.Matches(glob, p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

    public FileChange WriteText(string rel, string content) => _m.Write(_nodeId, _boundary, rel, new UTF8Encoding(false).GetBytes(content));
    public FileChange CopyFrom(string absoluteSourcePath, string rel) => _m.Write(_nodeId, _boundary, rel, File.ReadAllBytes(absoluteSourcePath));
    public void Remove(string rel) => _m.RemoveFile(_nodeId, _boundary, rel);

    public void CopyTo(string absoluteDestDir)
    {
        foreach (var rel in List())
        {
            var dest = Path.Combine(absoluteDestDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(_m.Root, rel), dest, overwrite: true);
        }
    }

    public IReadOnlyList<FileChange> ChangedFiles => _m.Changes(_nodeId);
}

public static class Glob
{
    public static bool IsExcludedDir(string rel) =>
        Regex.IsMatch(rel, @"(^|/)(bin|obj|\.git)/");

    public static bool Matches(string glob, string path)
    {
        var rx = "^" + Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(path, rx, RegexOptions.IgnoreCase);
    }

    public static bool MatchesAny(IEnumerable<string> globs, string path) => globs.Any(g => Matches(g, path));
}
