using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.State;
using Relay.Core.Storage;

namespace Relay.Core.Captures;

/// <summary>
/// The in-progress capture, rewritten atomically as text arrives so a crash mid-dictation loses
/// at most one debounce interval of text. Exactly one draft exists at a time.
/// </summary>
public sealed class CaptureDraft
{
    [JsonPropertyName("captureId")] public required string CaptureId { get; init; }
    [JsonPropertyName("mode")] public required string ModeWire { get; init; }
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>Process name that was foreground when the capture began (never the window title).</summary>
    [JsonPropertyName("previousForegroundProcess")] public string? PreviousForegroundProcess { get; init; }

    [JsonIgnore] public CaptureMode Mode => RelayStateExtensions.ParseMode(ModeWire);
}

public interface IDraftStore
{
    void Write(CaptureDraft draft);
    CaptureDraft? ReadCurrent();
    /// <summary>Removes the staging copy after its content has been durably committed to the ledger, or after the user cancelled.</summary>
    void RemoveCurrent();
    /// <summary>Moves an interrupted draft the user declined to keep into the discarded folder. Returns the new path.</summary>
    string MoveCurrentToDiscarded(string captureId);
    string CurrentPath { get; }
}

public sealed class FileDraftStore : IDraftStore
{
    private readonly DataRoot _root;

    public FileDraftStore(DataRoot root)
    {
        _root = root;
    }

    public string CurrentPath => _root.CurrentDraftPath;

    public void Write(CaptureDraft draft)
    {
        AtomicFile.WriteAllText(_root.CurrentDraftPath, JsonSerializer.Serialize(draft, RelayJson.Indented));
    }

    public CaptureDraft? ReadCurrent()
    {
        var text = AtomicFile.ReadAllTextIfExists(_root.CurrentDraftPath);
        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<CaptureDraft>(text, RelayJson.Indented);
        }
        catch (JsonException)
        {
            // An unreadable draft is still evidence; keep it beside the discarded drafts for inspection.
            Directory.CreateDirectory(_root.DiscardedDraftsDirectory);
            var target = Path.Combine(_root.DiscardedDraftsDirectory, $"unreadable-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            File.Move(_root.CurrentDraftPath, target, overwrite: false);
            return null;
        }
    }

    public void RemoveCurrent()
    {
        if (File.Exists(_root.CurrentDraftPath)) File.Delete(_root.CurrentDraftPath);
    }

    public string MoveCurrentToDiscarded(string captureId)
    {
        Directory.CreateDirectory(_root.DiscardedDraftsDirectory);
        var target = Path.Combine(_root.DiscardedDraftsDirectory, $"{captureId}.json");
        File.Move(_root.CurrentDraftPath, target, overwrite: false);
        return target;
    }
}
