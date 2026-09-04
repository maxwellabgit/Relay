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
    [property: JsonPropertyName("spans")] IReadOnlyList<SourceSpan> Spans)
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
}

public sealed class FileDraftNoteStore : IDraftNoteStore
{
    private readonly DataRoot _root;

    public FileDraftNoteStore(DataRoot root)
    {
        _root = root;
    }

    public string Write(DraftNote note)
    {
        Directory.CreateDirectory(_root.DraftNotesDirectory);
        var path = Path.Combine(_root.DraftNotesDirectory, note.NoteId + ".json");
        if (File.Exists(path)) throw new IOException($"Draft note {note.NoteId} already exists.");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(note, RelayJson.Indented));
        return path;
    }
}
