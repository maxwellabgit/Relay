using Relay.Core.Projects;
using Relay.Core.Storage;

namespace Relay.Core.Notes;

public sealed record NoteWriteResult(string Path, string Sha256, int Version, string? PreviousVersionPath, string? PreviousSha256);

public sealed record NoteReadProblem(string Path, string Reason);

/// <summary>
/// Reads and writes canonical note files inside a project folder. New notes never overwrite;
/// modifications copy the previous file into <c>.orchestrator\versions\{id}\{n}.md</c> first, so
/// no version of any note is ever lost (memory rule 6).
/// </summary>
public static class ProjectNoteStore
{
    public static string PathFor(string projectRoot, NoteDocument note) => Path.Combine(projectRoot, note.RelativePath);

    public static NoteWriteResult WriteNew(string projectRoot, NoteDocument note)
    {
        var path = PathFor(projectRoot, note);
        if (File.Exists(path)) throw new IOException($"Note {note.Id} already exists at {path}.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, note.ToMarkdown());
        return new NoteWriteResult(path, note.Sha256(), 1, null, null);
    }

    /// <summary>Replaces an existing note after preserving the current file as the next numbered version.</summary>
    public static NoteWriteResult WriteVersion(string projectRoot, NoteDocument updated)
    {
        var existing = Find(projectRoot, updated.Id) ?? throw new FileNotFoundException($"Note {updated.Id} does not exist in {projectRoot}.");
        var currentText = File.ReadAllText(existing.Path);
        var versionsDir = Path.Combine(ProjectLayout.VersionsDirectory(projectRoot), updated.Id);
        Directory.CreateDirectory(versionsDir);
        var next = Directory.EnumerateFiles(versionsDir, "*.md").Count() + 1;
        var versionPath = Path.Combine(versionsDir, $"{next}.md");
        AtomicFile.WriteAllText(versionPath, currentText);

        var newPath = PathFor(projectRoot, updated);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        AtomicFile.WriteAllText(newPath, updated.ToMarkdown());
        if (!string.Equals(newPath, existing.Path, StringComparison.OrdinalIgnoreCase))
        {
            // Type changed folder: keep the old file as a version, remove the stale live copy (its content is preserved above).
            File.Move(existing.Path, Path.Combine(versionsDir, $"{next}-moved.md"), overwrite: false);
        }
        return new NoteWriteResult(newPath, updated.Sha256(), next + 1, versionPath, Sha256Of(currentText));
    }

    public static (NoteDocument Note, string Path)? Find(string projectRoot, string noteId)
    {
        foreach (var folder in new[] { "notes", "decisions", "tasks" })
        {
            var path = Path.Combine(projectRoot, folder, noteId + ".md");
            if (File.Exists(path)) return (NoteDocument.Parse(File.ReadAllText(path)), path);
        }
        return null;
    }

    public static (IReadOnlyList<(NoteDocument Note, string Path)> Notes, IReadOnlyList<NoteReadProblem> Problems) ReadAll(string projectRoot)
    {
        var notes = new List<(NoteDocument, string)>();
        var problems = new List<NoteReadProblem>();
        foreach (var folder in new[] { "notes", "decisions", "tasks" })
        {
            var dir = Path.Combine(projectRoot, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                try { notes.Add((NoteDocument.Parse(File.ReadAllText(file)), file)); }
                catch (Exception ex) when (ex is FormatException or IOException) { problems.Add(new NoteReadProblem(file, ex.Message)); }
            }
        }
        return (notes, problems);
    }

    private static string Sha256Of(string text)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
