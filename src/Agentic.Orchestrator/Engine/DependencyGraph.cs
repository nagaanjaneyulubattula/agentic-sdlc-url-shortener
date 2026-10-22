using System.Text;

namespace Agentic.Orchestrator;

public sealed class GraphException(string message) : Exception(message);

/// <summary>
/// Explicit dependency graph (DAG) of SDLC nodes. Supports validation (duplicates, unknown deps, cycles),
/// topological layering (= parallel groups with synchronisation points), transitive dependents and critical-path analysis.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, NodeSpec> _nodes;

    public DependencyGraph(IEnumerable<NodeSpec> nodes)
    {
        _nodes = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (!_nodes.TryAdd(n.Id, n)) throw new GraphException($"Duplicate node id '{n.Id}'.");
        }
    }

    public IReadOnlyCollection<NodeSpec> Nodes => _nodes.Values;
    public NodeSpec this[string id] => _nodes[id];
    public bool Contains(string id) => _nodes.ContainsKey(id);

    /// <summary>Throws <see cref="GraphException"/> on unknown dependencies or cycles.</summary>
    public void Validate()
    {
        foreach (var n in _nodes.Values)
            foreach (var d in n.Deps)
                if (!_nodes.ContainsKey(d)) throw new GraphException($"Node '{n.Id}' depends on unknown node '{d}'.");
        var layers = Layers(); // throws on cycle
        _ = layers;
    }

    /// <summary>Kahn layering. Nodes in the same layer have no path between them and may run in parallel; the next layer is a synchronisation barrier.</summary>
    public List<List<string>> Layers()
    {
        var indegree = _nodes.Values.ToDictionary(n => n.Id, n => n.Deps.Count(_nodes.ContainsKey), StringComparer.Ordinal);
        var dependents = _nodes.Values.SelectMany(n => n.Deps.Where(_nodes.ContainsKey).Select(d => (dep: d, node: n.Id)))
            .GroupBy(x => x.dep).ToDictionary(g => g.Key, g => g.Select(x => x.node).ToList(), StringComparer.Ordinal);
        var layers = new List<List<string>>();
        var frontier = indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var seen = 0;
        while (frontier.Count > 0)
        {
            layers.Add(frontier);
            seen += frontier.Count;
            var next = new List<string>();
            foreach (var id in frontier)
                if (dependents.TryGetValue(id, out var ds))
                    foreach (var d in ds) if (--indegree[d] == 0) next.Add(d);
            frontier = next.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        if (seen != _nodes.Count)
        {
            var cyc = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal);
            throw new GraphException("Dependency cycle detected involving: " + string.Join(", ", cyc));
        }
        return layers;
    }

    public HashSet<string> TransitiveDependents(string id)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var n in _nodes.Values.Where(n => n.Deps.Contains(cur)))
                if (result.Add(n.Id)) stack.Push(n.Id);
        }
        return result;
    }

    /// <summary>Longest path by node weight (ms). Returns the total and the node sequence.</summary>
    public (double Total, List<string> Path) CriticalPath(Func<string, double> weight)
    {
        var best = new Dictionary<string, (double cost, string? prev)>(StringComparer.Ordinal);
        foreach (var layer in Layers())
            foreach (var id in layer)
            {
                var deps = _nodes[id].Deps.Where(best.ContainsKey).ToList();
                var (cost, prev) = deps.Count == 0 ? (0d, (string?)null) : deps.Select(d => (best[d].cost, (string?)d)).MaxBy(x => x.Item1);
                best[id] = (cost + weight(id), prev);
            }
        if (best.Count == 0) return (0, new());
        var end = best.MaxBy(kv => kv.Value.cost);
        var path = new List<string>();
        for (string? cur = end.Key; cur is not null; cur = best[cur].prev) path.Insert(0, cur);
        return (end.Value.cost, path);
    }

    public string ToMermaid(Func<string, string>? statusOf = null)
    {
        var sb = new StringBuilder("flowchart LR\n");
        foreach (var n in _nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
            sb.AppendLine($"  {Safe(n.Id)}[\"{n.Id}{(statusOf is null ? "" : "<br/>" + statusOf(n.Id))}\"]");
        foreach (var n in _nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
            foreach (var d in n.Deps.Where(_nodes.ContainsKey)) sb.AppendLine($"  {Safe(d)} --> {Safe(n.Id)}");
        return sb.ToString();
    }

    private static string Safe(string id) => new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
