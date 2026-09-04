using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Workspaces;

public sealed record WorkspaceRoot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt,
    [property: JsonPropertyName("label")] string? Label);

public sealed class WorkspaceRootsFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("roots")] public List<WorkspaceRoot> Roots { get; set; } = new();
}

/// <summary>
/// Registered workspace roots: the only folders project operations may touch. Added and removed
/// only through the UI (never by a proposal). The data root's own <c>archive\</c> folder is an
/// implicit root so archiving works before any workspace is registered.
/// </summary>
public sealed class WorkspaceRoots
{
    private readonly DataRoot _root;
    private WorkspaceRootsFile _file;

    public WorkspaceRoots(DataRoot root)
    {
        _root = root;
        _file = Load();
    }

    public IReadOnlyList<WorkspaceRoot> Registered => _file.Roots;

    /// <summary>Canonical roots including the implicit archive folder.</summary>
    public IReadOnlyList<string> CanonicalRoots
    {
        get
        {
            var list = new List<string>();
            foreach (var r in _file.Roots)
            {
                try { list.Add(PathGuard.Canonicalize(r.Path)); } catch (ArgumentException) { }
            }
            list.Add(PathGuard.Canonicalize(_root.ArchiveDirectory));
            return list;
        }
    }

    public PathCheck Check(string path) => PathGuard.Check(path, CanonicalRoots);

    public WorkspaceRoot Add(string path, string? label, DateTimeOffset now)
    {
        var canonical = PathGuard.Canonicalize(path);
        if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException($"Workspace root does not exist: {canonical}");
        if (PathGuard.IsWithin(canonical, PathGuard.Canonicalize(_root.Path)))
            throw new ArgumentException("The Relay data root cannot be registered as a workspace.");
        if (_file.Roots.Any(r => string.Equals(r.Path, canonical, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That folder is already registered.");
        var root = new WorkspaceRoot(canonical, now, label);
        _file.Roots.Add(root);
        Save();
        return root;
    }

    public bool Remove(string path)
    {
        var canonical = PathGuard.Canonicalize(path);
        var removed = _file.Roots.RemoveAll(r => string.Equals(r.Path, canonical, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        return removed > 0;
    }

    private WorkspaceRootsFile Load()
    {
        var text = AtomicFile.ReadAllTextIfExists(_root.WorkspacesPath);
        if (text is null) return new WorkspaceRootsFile();
        try { return JsonSerializer.Deserialize<WorkspaceRootsFile>(text, RelayJson.Indented) ?? new WorkspaceRootsFile(); }
        catch (JsonException) { return new WorkspaceRootsFile(); }
    }

    private void Save() => AtomicFile.WriteAllText(_root.WorkspacesPath, JsonSerializer.Serialize(_file, RelayJson.Indented));
}
