using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relay.Core.Storage;

namespace Relay.Core.Projects;

public static class Slug
{
    private static readonly Regex Valid = new("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.Compiled);

    public static bool IsValid(string? slug) => slug is not null && Valid.IsMatch(slug);

    /// <summary>Lower-cases, replaces runs of non-alphanumerics with '-', trims to 64 chars.</summary>
    public static string From(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = Regex.Replace(new string(chars), "-+", "-").Trim('-');
        if (collapsed.Length > 64) collapsed = collapsed[..64].TrimEnd('-');
        return collapsed;
    }
}

public sealed class ProjectPolicy
{
    [JsonPropertyName("allowWorkers")] public bool AllowWorkers { get; set; } = true;
    [JsonPropertyName("allowNetwork")] public bool AllowNetwork { get; set; }
    /// <summary>Notes below this routing confidence go to Review instead of being filed automatically.</summary>
    [JsonPropertyName("autoRouteThreshold")] public double? AutoRouteThreshold { get; set; }
}

public sealed class ProjectRecord
{
    public const string ActiveStatus = "active";
    public const string ArchivedStatus = "archived";
    /// <summary>Deleted on the user's direct, approved request. The record stays so the history remains readable; the folder is gone.</summary>
    public const string DeletedStatus = "deleted";

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("slug")] public required string Slug { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = ActiveStatus;
    [JsonPropertyName("rootPath")] public required string RootPath { get; set; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("archivedAt")] public DateTimeOffset? ArchivedAt { get; set; }
    [JsonPropertyName("archivedPath")] public string? ArchivedPath { get; set; }
    [JsonPropertyName("policy")] public ProjectPolicy Policy { get; set; } = new();

    [JsonIgnore] public bool IsActive => Status == ActiveStatus;

    public bool Matches(string nameOrSlugOrAlias)
    {
        var q = nameOrSlugOrAlias.Trim();
        return string.Equals(q, Id, StringComparison.Ordinal)
            || string.Equals(q, Slug, StringComparison.OrdinalIgnoreCase)
            || string.Equals(q, Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Projects.Slug.From(q), Slug, StringComparison.Ordinal)
            || Aliases.Any(a => string.Equals(a, q, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ProjectRegistryFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("projects")] public List<ProjectRecord> Projects { get; set; } = new();
}

/// <summary>
/// Current state of every project Relay knows about. History lives in the ledger
/// (<c>project.*</c> events); this file is the fast lookup rebuilt from those events if lost.
/// </summary>
public sealed class ProjectRegistry
{
    private readonly DataRoot _root;
    private ProjectRegistryFile _file;

    public ProjectRegistry(DataRoot root)
    {
        _root = root;
        _file = Load();
    }

    public IReadOnlyList<ProjectRecord> All => _file.Projects;
    public IEnumerable<ProjectRecord> Active => _file.Projects.Where(p => p.IsActive);

    public ProjectRecord? Find(string idSlugNameOrAlias) => _file.Projects.FirstOrDefault(p => p.Matches(idSlugNameOrAlias));
    public ProjectRecord? FindActive(string idSlugNameOrAlias) => _file.Projects.FirstOrDefault(p => p.IsActive && p.Matches(idSlugNameOrAlias));
    public ProjectRecord? ById(string id) => _file.Projects.FirstOrDefault(p => p.Id == id);

    public bool SlugInUse(string slug) => _file.Projects.Any(p => p.IsActive && string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public void Add(ProjectRecord record)
    {
        if (ById(record.Id) is not null) throw new InvalidOperationException($"Project {record.Id} already exists.");
        if (SlugInUse(record.Slug)) throw new InvalidOperationException($"Slug '{record.Slug}' is already in use by an active project.");
        _file.Projects.Add(record);
        Save();
    }

    public void Update(ProjectRecord record)
    {
        var index = _file.Projects.FindIndex(p => p.Id == record.Id);
        if (index < 0) throw new InvalidOperationException($"Project {record.Id} is not registered.");
        _file.Projects[index] = record;
        Save();
    }

    public void Reload() => _file = Load();

    private ProjectRegistryFile Load()
    {
        var text = AtomicFile.ReadAllTextIfExists(_root.ProjectsRegistryPath);
        if (text is null) return new ProjectRegistryFile();
        try { return JsonSerializer.Deserialize<ProjectRegistryFile>(text, RelayJson.Indented) ?? new ProjectRegistryFile(); }
        catch (JsonException) { return new ProjectRegistryFile(); }
    }

    private void Save() => AtomicFile.WriteAllText(_root.ProjectsRegistryPath, JsonSerializer.Serialize(_file, RelayJson.Indented));
}
