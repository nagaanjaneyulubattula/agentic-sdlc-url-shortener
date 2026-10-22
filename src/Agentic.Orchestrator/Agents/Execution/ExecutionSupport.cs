using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentic.Orchestrator.Agents;

/// <summary>A committed file change of the run as published by the run snapshot's State().changes array.</summary>
internal sealed record RunChange(string Path, string Op, string NodeId);

/// <summary>Shared helpers for the execution / verification / reporting agents: workspace op tracking, markdown rendering.</summary>
internal static class ExecutionSupport
{
    // ───────────── workspace helpers ─────────────

    /// <summary>Writes text and reports an accurate Op (create | modify | unchanged) computed BEFORE the write. Identical content is not rewritten.</summary>
    public static FileChange WriteTracked(IWorkspace ws, string rel, string content)
    {
        var existed = ws.Exists(rel);
        var prior = existed ? ws.ReadText(rel) : null;
        if (existed && string.Equals(prior, content, StringComparison.Ordinal))
            return new FileChange(rel, "unchanged", ws.Sha256(rel), Encoding.UTF8.GetByteCount(content));
        var change = ws.WriteText(rel, content);
        return change with { Op = existed ? "modify" : "create" };
    }

    /// <summary>
    /// Copies a library file into the workspace. <paramref name="priorText"/> is the content the workspace had before this node started
    /// (null = did not exist), which lets clean-room reinstalls report ops relative to the original state.
    /// </summary>
    public static FileChange CopyTracked(IWorkspace ws, string absoluteSource, string rel, string? priorText)
    {
        var source = File.ReadAllText(absoluteSource);
        var op = priorText is null ? "create" : string.Equals(priorText, source, StringComparison.Ordinal) ? "unchanged" : "modify";
        if (ws.Exists(rel) && string.Equals(ws.ReadText(rel), source, StringComparison.Ordinal))
            return new FileChange(rel, op, ws.Sha256(rel), new FileInfo(absoluteSource).Length);
        var change = ws.CopyFrom(absoluteSource, rel);
        return change with { Op = op };
    }

    public static string SliceSrcDir(FeatureCatalog cat, FeatureDef f) => cat.Core.SliceSrcDir.Replace("{Slice}", f.Slice).TrimEnd('/');
    public static string SliceTestDir(FeatureCatalog cat, FeatureDef f) => cat.Core.SliceTestDir.Replace("{Slice}", f.Slice).TrimEnd('/');

    /// <summary>Workspace files below a directory (workspace-relative, '/'-separated). Implemented via a full listing so it does not depend on glob corner cases.</summary>
    public static List<string> FilesUnder(IWorkspace ws, string dir)
    {
        var prefix = dir.TrimEnd('/') + "/";
        return ws.List("**/*").Select(p => p.Replace('\\', '/')).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public static bool IsSlice(FeatureDef f) => !f.IsPatch && !string.IsNullOrEmpty(f.Slice);

    public static bool HasSliceFiles(IWorkspace ws, FeatureCatalog cat, FeatureDef f) =>
        IsSlice(f) && FilesUnder(ws, SliceSrcDir(cat, f)).Count > 0;

    // ───────────── run snapshot helpers ─────────────

    public static List<RunChange> ReadChanges(JsonNode? state)
    {
        var list = new List<RunChange>();
        foreach (var item in JsonView.Items(JsonView.Prop(state, "changes")))
        {
            var path = JsonView.Str(item, "path", "file");
            if (string.IsNullOrEmpty(path)) continue;
            list.Add(new RunChange(path.Replace('\\', '/'), JsonView.Str(item, "op", "operation").ToLowerInvariant(), JsonView.Str(item, "nodeId", "node")));
        }
        return list;
    }

    /// <summary>Net effect of the committed changes per path: created (any create), modified (modify only), deleted (last op delete). Unchanged ops are ignored.</summary>
    public static (List<(string Path, string NodeId)> Created, List<(string Path, string NodeId)> Modified, List<(string Path, string NodeId)> Deleted) NetChanges(IEnumerable<RunChange> changes)
    {
        var created = new List<(string, string)>();
        var modified = new List<(string, string)>();
        var deleted = new List<(string, string)>();
        foreach (var g in changes.Where(c => c.Op is "create" or "modify" or "delete").GroupBy(c => c.Path).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ops = g.ToList();
            if (ops[^1].Op == "delete") deleted.Add((g.Key, ops[^1].NodeId));
            else if (ops.Any(o => o.Op == "create")) created.Add((g.Key, ops.First(o => o.Op == "create").NodeId));
            else modified.Add((g.Key, string.Join(", ", ops.Select(o => o.NodeId).Where(n => n.Length > 0).Distinct())));
        }
        return (created, modified, deleted);
    }

    // ───────────── markdown helpers ─────────────

    public static string Cell(string? s) =>
        (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ").Trim();

    public static string Table(string[] headers, IEnumerable<string?[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "_None._\n";
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers.Select(Cell))).AppendLine(" |");
        sb.Append("|").Append(string.Concat(headers.Select(_ => " --- |"))).AppendLine();
        foreach (var row in list)
            sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
        return sb.ToString();
    }

    public static string Bullets(IEnumerable<string> items, string empty = "_None._")
    {
        var list = items.ToList();
        return list.Count == 0 ? empty + "\n" : string.Join("\n", list.Select(i => "- " + i)) + "\n";
    }

    public static string Inv(double d, string format = "0.##") => d.ToString(format, CultureInfo.InvariantCulture);

    public static string Percent(double ratio) => (ratio * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

/// <summary>Tolerant, case/format-insensitive JSON access for run-snapshot payloads whose exact shape is owned by the engine.</summary>
internal static class JsonView
{
    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Direct property lookup; the first of <paramref name="names"/> that exists wins ("node_id" == "nodeId" == "NodeId").</summary>
    public static JsonNode? Prop(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject o) return null;
        foreach (var name in names)
        {
            var want = Norm(name);
            foreach (var kv in o)
                if (kv.Value is not null && Norm(kv.Key) == want) return kv.Value;
        }
        return null;
    }

    /// <summary>Breadth-first search for the first property with one of the given names anywhere below <paramref name="node"/>.</summary>
    public static JsonNode? Deep(JsonNode? node, params string[] names)
    {
        var queue = new Queue<JsonNode>();
        if (node is not null) queue.Enqueue(node);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            var hit = Prop(n, names);
            if (hit is not null) return hit;
            switch (n)
            {
                case JsonObject o: foreach (var kv in o) if (kv.Value is not null) queue.Enqueue(kv.Value); break;
                case JsonArray a: foreach (var i in a) if (i is not null) queue.Enqueue(i); break;
            }
        }
        return null;
    }

    public static string Str(JsonNode? node, params string[] names) => Text(Prop(node, names));

    public static double? Num(JsonNode? n)
    {
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (n is JsonValue s && s.GetValueKind() == JsonValueKind.String && double.TryParse(s.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return d2;
        return null;
    }

    public static IEnumerable<JsonNode> Items(JsonNode? n) =>
        n switch
        {
            JsonArray a => a.Where(i => i is not null).Select(i => i!),
            JsonObject o => o.Where(kv => kv.Value is not null).Select(kv => kv.Value!),
            _ => Enumerable.Empty<JsonNode>(),
        };

    public static IEnumerable<(string? Key, JsonNode Value)> Entries(JsonNode? n) =>
        n switch
        {
            JsonArray a => a.Where(i => i is not null).Select(i => ((string?)null, i!)),
            JsonObject o => o.Where(kv => kv.Value is not null).Select(kv => ((string?)kv.Key, kv.Value!)),
            _ => Enumerable.Empty<(string?, JsonNode)>(),
        };

    /// <summary>Human-readable rendering of any node (scalars as-is, arrays comma separated, objects as key=value pairs).</summary>
    public static string Text(JsonNode? n) =>
        n switch
        {
            null => "",
            JsonValue v => Scalar(v),
            JsonArray a => string.Join(", ", a.Select(Text)),
            JsonObject o => string.Join(", ", o.Select(kv => $"{kv.Key}={Text(kv.Value)}")),
            _ => "",
        };

    private static string Scalar(JsonValue v) =>
        v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : v.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
}
