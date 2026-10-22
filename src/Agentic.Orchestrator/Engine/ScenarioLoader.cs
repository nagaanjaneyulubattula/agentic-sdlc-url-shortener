using System.Text.Json;

namespace Agentic.Orchestrator;

public static class ScenarioLoader
{
    public static IReadOnlyList<ScenarioSpec> LoadAll(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "scenarios");
        if (!Directory.Exists(dir)) return Array.Empty<ScenarioSpec>();
        return Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Parse(File.ReadAllText(p))).ToList();
    }

    public static ScenarioSpec Parse(string json) =>
        JsonSerializer.Deserialize<ScenarioSpec>(json, Json.Options) ?? throw new InvalidDataException("Empty scenario file");

    /// <summary>Fresh, independent copy (runs mutate nothing, but faults may be cleared on resume).</summary>
    public static ScenarioSpec Clone(ScenarioSpec s) => Parse(Json.Serialize(s));

    public static ScenarioSpec Load(string repoRoot, string id) =>
        LoadAll(repoRoot).FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"Unknown scenario '{id}'.");
}
