using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Memory;

public static class GlossaryScopes
{
    public const string Project = "project";
    public const string Global = "global";
}

public sealed class GlossaryEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("acronym")] public required string Acronym { get; init; }
    [JsonPropertyName("expansion")] public required string Expansion { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("projectId")] public string? ProjectId { get; init; }
    [JsonPropertyName("sourceRefs")] public List<string> SourceRefs { get; init; } = [];
}

/// <summary>Project + global glossary JSON. Resolve is read-only in v0.1.</summary>
public sealed class GlossaryStore
{
    private readonly DataRoot _root;

    public GlossaryStore(DataRoot root) => _root = root;

    public string GlobalGlossaryPath => Path.Combine(_root.ConfigDirectory, "glossary.json");

    public static string ProjectGlossaryPath(string projectRoot) =>
        Path.Combine(projectRoot, ProjectLayoutRelative);

    public const string ProjectLayoutRelative = ".orchestrator/glossary.json";

    public IReadOnlyList<GlossaryEntry> LoadGlobal() => LoadFile(GlobalGlossaryPath);

    public IReadOnlyList<GlossaryEntry> LoadProject(string projectRoot) =>
        LoadFile(ProjectGlossaryPath(projectRoot));

    public void SaveGlobal(IEnumerable<GlossaryEntry> entries) =>
        SaveFile(GlobalGlossaryPath, entries);

    public void SaveProject(string projectRoot, IEnumerable<GlossaryEntry> entries) =>
        SaveFile(ProjectGlossaryPath(projectRoot), entries);

    public IReadOnlyList<GlossaryEntry> FindExact(
        string acronym,
        string? projectId,
        string? projectRoot)
    {
        var key = Normalize(acronym);
        var hits = new List<GlossaryEntry>();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            hits.AddRange(LoadProject(projectRoot)
                .Where(e => string.Equals(Normalize(e.Acronym), key, StringComparison.Ordinal)
                            && e.Scope == GlossaryScopes.Project
                            && (projectId is null || e.ProjectId is null
                                || string.Equals(e.ProjectId, projectId, StringComparison.Ordinal))));
        }

        hits.AddRange(LoadGlobal()
            .Where(e => string.Equals(Normalize(e.Acronym), key, StringComparison.Ordinal)));
        return hits;
    }

    public static string Normalize(string acronym) =>
        acronym.Trim().ToUpperInvariant();

    private static IReadOnlyList<GlossaryEntry> LoadFile(string path)
    {
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var list = JsonSerializer.Deserialize<List<GlossaryEntry>>(json, RelayJson.Compact);
        return list ?? [];
    }

    private static void SaveFile(string path, IEnumerable<GlossaryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entries.ToList(), RelayJson.Indented));
    }
}
