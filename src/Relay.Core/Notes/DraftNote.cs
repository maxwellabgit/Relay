using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Notes;

/// <summary>
/// A note stored in staging without model interpretation (contract §15.8). It is the verbatim
/// capture plus a source span back to the ledger record that holds the original text, so every
/// later extraction, routing decision, or summary can be traced to the words that produced it.
/// </summary>
public sealed record DraftNote(
    [property: JsonPropertyName("noteId")] string NoteId,
    [property: JsonPropertyName("captureId")] string CaptureId,
    [property: JsonPropertyName("sourceEventId")] string SourceEventId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("routing")] string Routing,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("spans")] IReadOnlyList<SourceSpan> Spans,
    /// <summary>The label the extractor or judge gave the note; travels with the draft so the ledger's routing records need not repeat words from it.</summary>
    [property: JsonPropertyName("topic")] string? Topic = null)
{
    public const string RawCaptureType = "raw-capture";
    public const string DraftStatus = "draft";
    public const string UnroutedRouting = "unrouted";
}

/// <summary>A half-open character range inside the text of one ledger record.</summary>
public sealed record SourceSpan(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public interface IDraftNoteStore
{
    /// <summary>Writes the note and returns its path. Never overwrites an existing note.</summary>
    string Write(DraftNote note);

    /// <summary>Reads a draft note by id from staging (unrouted or routed), or null.</summary>
    DraftNote? Read(string noteId);

    /// <summary>All draft notes still waiting in staging, oldest first.</summary>
    IReadOnlyList<DraftNote> Unrouted();

    /// <summary>Records that the note now lives in a project and moves the staging copy to <c>staging\notes\routed</c>. Returns the new path.</summary>
    string MarkRouted(string noteId, string projectId, double? confidence);
}

public sealed class FileDraftNoteStore : IDraftNoteStore
{
    public const string RoutedRouting = "routed";

    private readonly DataRoot _root;

    public FileDraftNoteStore(DataRoot root)
    {
        _root = root;
    }

    private string RoutedDirectory => Path.Combine(_root.DraftNotesDirectory, "routed");

    public string Write(DraftNote note)
    {
        Directory.CreateDirectory(_root.DraftNotesDirectory);
        var path = Path.Combine(_root.DraftNotesDirectory, note.NoteId + ".json");
        if (File.Exists(path)) throw new IOException($"Draft note {note.NoteId} already exists.");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(note, RelayJson.Indented));
        return path;
    }

    public DraftNote? Read(string noteId)
    {
        foreach (var dir in new[] { _root.DraftNotesDirectory, RoutedDirectory })
        {
            var path = Path.Combine(dir, noteId + ".json");
            var text = AtomicFile.ReadAllTextIfExists(path);
            if (text is null) continue;
            try { return JsonSerializer.Deserialize<DraftNote>(text, RelayJson.Indented); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    public IReadOnlyList<DraftNote> Unrouted()
    {
        if (!Directory.Exists(_root.DraftNotesDirectory)) return [];
        var notes = new List<DraftNote>();
        foreach (var file in Directory.EnumerateFiles(_root.DraftNotesDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var note = JsonSerializer.Deserialize<DraftNote>(File.ReadAllText(file), RelayJson.Indented);
                if (note is not null) notes.Add(note);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* unreadable drafts are reported by recovery, not guessed here */ }
        }
        return notes;
    }

    public string MarkRouted(string noteId, string projectId, double? confidence)
    {
        var note = Read(noteId) ?? throw new FileNotFoundException($"Draft note {noteId} does not exist.");
        var updated = note with { Routing = RoutedRouting, ProjectId = projectId, Confidence = confidence ?? note.Confidence };
        Directory.CreateDirectory(RoutedDirectory);
        var target = Path.Combine(RoutedDirectory, noteId + ".json");
        AtomicFile.WriteAllText(target, JsonSerializer.Serialize(updated, RelayJson.Indented));
        var original = Path.Combine(_root.DraftNotesDirectory, noteId + ".json");
        if (File.Exists(original)) File.Delete(original);
        return target;
    }
}
