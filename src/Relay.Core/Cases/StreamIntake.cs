using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>Persisted listening-session pointer so an interrupted stream can resume after restart.</summary>
public sealed class ListeningSessionState
{
    [JsonPropertyName("caseId")] public string? CaseId { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("ingestedSegmentIds")] public List<string> IngestedSegmentIds { get; set; } = [];
    /// <summary>Segments written to the object store and case log but not yet stepped by the mind.</summary>
    [JsonPropertyName("pendingMindSegmentIds")] public List<string> PendingMindSegmentIds { get; set; } = [];
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One ingested transcript segment as presented to the mind (text loaded from the object store).</summary>
public sealed record ListeningSegmentView(
    string SegmentId,
    string EventId,
    string ObjectId,
    string Sha256,
    DateTimeOffset Ts,
    string? Speaker,
    string Text);

/// <summary>
/// Dictation / Wispr Flow input adapter: persist timestamped segments to the object store first,
/// then open or extend a long-lived observed case. Not a second mind loop.
/// </summary>
public sealed class StreamIntake
{
    public const string PresentationListening = "listening";
    public const string SegmentObjectKind = "transcript.segment";

    private readonly DataRoot _root;
    private readonly ObjectStore _objects;
    private readonly CaseStore _cases;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public StreamIntake(DataRoot root, ObjectStore objects, CaseStore cases, IClock clock)
    {
        _root = root;
        _objects = objects;
        _cases = cases;
        _clock = clock;
    }

    public string SessionPath => Path.Combine(_root.StreamDirectory, "listening-session.json");

    public ListeningSessionState LoadState()
    {
        var text = AtomicFile.ReadAllTextIfExists(SessionPath);
        if (text is null) return new ListeningSessionState();
        return JsonSerializer.Deserialize<ListeningSessionState>(text, RelayJson.Indented) ?? new ListeningSessionState();
    }

    public void SaveState(ListeningSessionState state)
    {
        lock (_gate)
        {
            state.UpdatedAt = _clock.UtcNow;
            Directory.CreateDirectory(_root.StreamDirectory);
            AtomicFile.WriteAllText(SessionPath, JsonSerializer.Serialize(state, RelayJson.Indented));
        }
    }

    /// <summary>
    /// Writes segment bytes to the object store, then returns refs for the case event.
    /// Callers must persist the case event before treating the segment as ingested for the mind.
    /// </summary>
    public (string SegmentId, StoredObject Stored, object EventPayload) PrepareSegment(
        string text,
        DateTimeOffset ts,
        string? speaker,
        string? classification = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var segmentId = Ulid.NewUlid(_clock.UtcNow);
        var blob = new
        {
            kind = SegmentObjectKind,
            segmentId,
            ts,
            speaker,
            text,
        };
        // Persist content BEFORE the case marks the segment ingested.
        // Default: eligible for session-scoped hosted use once a grant exists (listening ≠ hosted).
        var stored = _objects.PutJson(
            blob,
            objectId: segmentId,
            classification: classification ?? Privacy.SourceClassification.HostedAllowedSession);
        var payload = new
        {
            segmentId,
            objectId = stored.ObjectId,
            sha256 = stored.Sha256,
            ts,
            speaker,
            charCount = text.Length,
            classification = classification ?? Privacy.SourceClassification.HostedAllowedSession,
        };
        return (segmentId, stored, payload);
    }

    public string? TryLoadSegmentText(string objectIdOrHash)
    {
        // Prefer by-id meta → hash path; fall back to scanning PutJson content via by-id.
        var metaPath = Path.Combine(_root.ObjectsDirectory, "by-id", objectIdOrHash + ".json");
        var metaText = AtomicFile.ReadAllTextIfExists(metaPath);
        if (metaText is not null)
        {
            using var doc = JsonDocument.Parse(metaText);
            if (doc.RootElement.TryGetProperty("sha256", out var hashEl))
            {
                var body = _objects.TryReadTextByHash(hashEl.GetString()!);
                if (body is not null) return ExtractText(body);
            }
        }

        // objectId was also used as content file in some layouts — try hash directly.
        var direct = _objects.TryReadTextByHash(objectIdOrHash);
        return direct is null ? null : ExtractText(direct);
    }

    private static string? ExtractText(string jsonOrText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOrText);
            if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        catch (JsonException)
        {
            return jsonOrText;
        }
        return null;
    }

    public IReadOnlyList<ListeningSegmentView> LoadRecentSegments(string caseId, int limit = 32)
    {
        var events = _cases.LoadEvents(caseId);
        var list = new List<ListeningSegmentView>();
        foreach (var evt in events)
        {
            if (evt.Type != CaseEventTypes.SegmentIngested) continue;
            var p = evt.Payload;
            var segmentId = p.TryGetProperty("segmentId", out var s) ? s.GetString() ?? "" : "";
            var objectId = p.TryGetProperty("objectId", out var o) ? o.GetString() ?? "" : "";
            var sha = p.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
            var speaker = p.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
            var ts = p.TryGetProperty("ts", out var tsEl) && tsEl.TryGetDateTimeOffset(out var dto) ? dto : evt.Ts;
            var text = TryLoadSegmentText(objectId) ?? "";
            list.Add(new ListeningSegmentView(segmentId, evt.EventId, objectId, sha, ts, speaker, text));
        }
        if (list.Count <= limit) return list;
        return list.TakeLast(limit).ToList();
    }
}
