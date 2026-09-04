using Relay.Core.Storage;

namespace Relay.Core.Projects;

public sealed record ArchiveResult(string OldPath, string NewPath, string ManifestPath, string ManifestHash, int Files, DateTimeOffset RecoverUntil);

/// <summary>
/// "Deleting" a project means moving it, whole, into the recoverable archive with a manifest of
/// every file hash (contract §10). Nothing here removes bytes. Restore moves it back and verifies
/// the manifest so a tampered archive is reported before it becomes canonical again.
/// </summary>
public static class ProjectArchiver
{
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(90);

    public static ArchiveResult Archive(ProjectRecord record, DataRoot root, DateTimeOffset now)
    {
        if (!record.IsActive) throw new InvalidOperationException("Project is already archived.");
        if (!Directory.Exists(record.RootPath)) throw new DirectoryNotFoundException(record.RootPath);
        Directory.CreateDirectory(root.ArchiveDirectory);

        var target = Path.Combine(root.ArchiveDirectory, $"{record.Slug}-{record.Id}");
        if (Directory.Exists(target)) throw new IOException($"Archive target already exists: {target}");

        var manifest = ProjectLayout.Manifest(record.RootPath, now);
        var manifestPath = ProjectLayout.WriteManifest(manifest, target + ".manifest.json");
        Directory.Move(record.RootPath, target);

        return new ArchiveResult(record.RootPath, target, manifestPath, manifest.Hash(), manifest.Entries.Count, now + RecoveryWindow);
    }

    public static (string RestoredPath, IReadOnlyList<string> Problems) Restore(ProjectRecord record, DateTimeOffset now)
    {
        if (record.IsActive || record.ArchivedPath is null) throw new InvalidOperationException("Project is not archived.");
        if (Directory.Exists(record.RootPath) && Directory.EnumerateFileSystemEntries(record.RootPath).Any())
            throw new IOException($"Original location is occupied: {record.RootPath}");

        var problems = new List<string>();
        var manifestPath = record.ArchivedPath + ".manifest.json";
        var text = AtomicFile.ReadAllTextIfExists(manifestPath);
        if (text is null) problems.Add("Archive manifest is missing; contents could not be verified.");
        else
        {
            var expected = System.Text.Json.JsonSerializer.Deserialize<FolderManifest>(text, RelayJson.Indented);
            var actual = ProjectLayout.Manifest(record.ArchivedPath, now);
            if (expected is null) problems.Add("Archive manifest is unreadable.");
            else
            {
                var expectedMap = expected.Entries.ToDictionary(e => e.RelativePath, e => e.Sha256, StringComparer.OrdinalIgnoreCase);
                foreach (var e in actual.Entries)
                {
                    if (!expectedMap.TryGetValue(e.RelativePath, out var hash)) problems.Add($"Unexpected file in archive: {e.RelativePath}");
                    else if (hash != e.Sha256) problems.Add($"Modified while archived: {e.RelativePath}");
                }
                foreach (var missing in expectedMap.Keys.Except(actual.Entries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase))
                    problems.Add($"Missing from archive: {missing}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(record.RootPath)!);
        if (Directory.Exists(record.RootPath)) Directory.Delete(record.RootPath); // empty placeholder only, checked above
        Directory.Move(record.ArchivedPath, record.RootPath);
        return (record.RootPath, problems);
    }
}
