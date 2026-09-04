using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Projects;

public sealed record ManifestEntry(
    [property: JsonPropertyName("path")] string RelativePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("length")] long Length);

public sealed record FolderManifest(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("entries")] IReadOnlyList<ManifestEntry> Entries)
{
    public string Hash()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal)) sb.Append(e.RelativePath).Append('\n').Append(e.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>The project folder contract (contract §10). Creation and verification only; nothing here deletes.</summary>
public static class ProjectLayout
{
    public const string OrchestratorDirectoryName = ".orchestrator";

    public static readonly string[] Directories = ["notes", "decisions", "tasks", "conversations", "artifacts", OrchestratorDirectoryName,
        Path.Combine(OrchestratorDirectoryName, "versions"), Path.Combine(OrchestratorDirectoryName, "staging")];

    public static string ProjectToml(string root) => Path.Combine(root, "project.toml");
    public static string Overview(string root) => Path.Combine(root, "overview.md");
    public static string ArtifactsManifest(string root) => Path.Combine(root, OrchestratorDirectoryName, "artifacts.jsonl");
    public static string SourcesFile(string root) => Path.Combine(root, OrchestratorDirectoryName, "sources.jsonl");
    public static string VersionsDirectory(string root) => Path.Combine(root, OrchestratorDirectoryName, "versions");

    /// <summary>Creates the folder tree. Fails if the root already exists and is not empty.</summary>
    public static void Create(ProjectRecord record, DateTimeOffset now)
    {
        var root = record.RootPath;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException($"Target folder is not empty: {root}");
        Directory.CreateDirectory(root);
        foreach (var dir in Directories) Directory.CreateDirectory(Path.Combine(root, dir));

        AtomicFile.WriteAllText(ProjectToml(root), RenderToml(record, now));
        AtomicFile.WriteAllText(Overview(root), $"# {record.Name}\n\nCreated {now:yyyy-MM-dd}. Relay project `{record.Slug}` ({record.Id}).\n\nThis overview is yours; Relay only proposes edits here.\n");
        AtomicFile.WriteAllText(ArtifactsManifest(root), "");
        AtomicFile.WriteAllText(SourcesFile(root), "");
    }

    public static string RenderToml(ProjectRecord record, DateTimeOffset now)
    {
        var aliases = string.Join(", ", record.Aliases.Select(a => Quote(a)));
        return $"""
            # Relay project — identity lives in `id`; the folder name may change, the id never does.
            id = {Quote(record.Id)}
            slug = {Quote(record.Slug)}
            name = {Quote(record.Name)}
            aliases = [{aliases}]
            status = {Quote(record.Status)}
            created = {Quote(record.CreatedAt.ToString("O"))}
            written = {Quote(now.ToString("O"))}

            [policy]
            allow_workers = {(record.Policy.AllowWorkers ? "true" : "false")}
            allow_network = {(record.Policy.AllowNetwork ? "true" : "false")}

            """;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Returns the missing required entries; empty when the folder matches the contract.</summary>
    public static IReadOnlyList<string> Verify(string root)
    {
        var missing = new List<string>();
        if (!Directory.Exists(root)) return [root];
        foreach (var dir in Directories) if (!Directory.Exists(Path.Combine(root, dir))) missing.Add(dir);
        if (!File.Exists(ProjectToml(root))) missing.Add("project.toml");
        if (!File.Exists(Overview(root))) missing.Add("overview.md");
        return missing;
    }

    /// <summary>Hashes every file beneath the root. Used before archive moves and for backups.</summary>
    public static FolderManifest Manifest(string root, DateTimeOffset now)
    {
        var entries = new List<ManifestEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            entries.Add(new ManifestEntry(Path.GetRelativePath(root, file), hash, stream.Length));
        }
        return new FolderManifest(root, now, entries);
    }

    public static string WriteManifest(FolderManifest manifest, string path)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(manifest, RelayJson.Indented));
        return path;
    }
}
