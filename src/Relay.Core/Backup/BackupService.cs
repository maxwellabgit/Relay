using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Projects;
using Relay.Core.Storage;

namespace Relay.Core.Backup;

public sealed record BackupManifest(
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("dataRoot")] string DataRoot,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> ProjectIds,
    [property: JsonPropertyName("entries")] IReadOnlyList<ManifestEntry> Entries);

public sealed record BackupResult(string ZipPath, int Files, string ManifestHash);
public sealed record BackupVerification(bool Ok, int Files, IReadOnlyList<string> Problems);

/// <summary>
/// Exports the data root plus every active project folder into one zip with a hashed manifest,
/// and verifies such a zip. Backups are the only sanctioned way content leaves the machine in
/// this phase, and export is a Tier B action when triggered by anything other than the user.
/// </summary>
public static class BackupService
{
    private const string ManifestName = "manifest.json";

    public static BackupResult Export(DataRoot root, IEnumerable<ProjectRecord> projects, string zipPath, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var temp = zipPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        var entries = new List<ManifestEntry>();
        var ids = new List<string>();
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            AddTree(zip, root.Path, "data-root", entries, skip: p => p.StartsWith(root.BackupsDirectory, StringComparison.OrdinalIgnoreCase));
            foreach (var project in projects.Where(p => p.IsActive && Directory.Exists(p.RootPath)))
            {
                ids.Add(project.Id);
                AddTree(zip, project.RootPath, $"projects/{project.Id}", entries, skip: _ => false);
            }
            var manifest = new BackupManifest(now, root.Path, ids, entries);
            var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, manifest, RelayJson.Indented);
        }
        File.Move(temp, zipPath, overwrite: false);
        return new BackupResult(zipPath, entries.Count, new FolderManifest(root.Path, now, entries).Hash());
    }

    private static void AddTree(ZipArchive zip, string folder, string prefix, List<ManifestEntry> entries, Func<string, bool> skip)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (skip(file)) continue;
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var name = prefix + "/" + relative;
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var target = entry.Open()) target.Write(bytes);
            entries.Add(new ManifestEntry(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
        }
    }

    public static BackupVerification Verify(string zipPath)
    {
        var problems = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestEntry = zip.GetEntry(ManifestName);
        if (manifestEntry is null) return new BackupVerification(false, 0, ["manifest.json is missing"]);
        BackupManifest? manifest;
        using (var stream = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<BackupManifest>(stream, RelayJson.Indented);
        if (manifest is null) return new BackupVerification(false, 0, ["manifest.json is unreadable"]);

        var expected = manifest.Entries.ToDictionary(e => e.RelativePath, e => e, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == ManifestName) continue;
            seen.Add(entry.FullName);
            if (!expected.TryGetValue(entry.FullName, out var e)) { problems.Add($"Not in manifest: {entry.FullName}"); continue; }
            using var stream = entry.Open();
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (hash != e.Sha256) problems.Add($"Hash mismatch: {entry.FullName}");
        }
        foreach (var missing in expected.Keys.Where(k => !seen.Contains(k))) problems.Add($"Missing from archive: {missing}");
        return new BackupVerification(problems.Count == 0, manifest.Entries.Count, problems);
    }
}
