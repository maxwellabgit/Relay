using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Sessions;

/// <summary>
/// Liveness record for one application run. A session file without <c>cleanShutdown</c> at the
/// next start means the previous process died; the new session records that in the ledger.
/// </summary>
public sealed class SessionRecord
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("pid")] public required int ProcessId { get; init; }
    [JsonPropertyName("appVersion")] public required string AppVersion { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("cleanShutdown")] public bool CleanShutdown { get; set; }
    [JsonPropertyName("endedBy")] public string? EndedBy { get; set; }
}

public sealed class SessionStore
{
    private readonly DataRoot _root;

    public SessionStore(DataRoot root)
    {
        _root = root;
    }

    public string PathFor(string sessionId) => Path.Combine(_root.SessionsDirectory, sessionId + ".json");

    public void Write(SessionRecord record)
    {
        AtomicFile.WriteAllText(PathFor(record.SessionId), JsonSerializer.Serialize(record, RelayJson.Indented));
    }

    /// <summary>Sessions that never recorded a clean shutdown. With the single-instance guard these are crashes.</summary>
    public IReadOnlyList<SessionRecord> FindUnclean(string? excludingSessionId = null)
    {
        if (!Directory.Exists(_root.SessionsDirectory)) return [];
        var result = new List<SessionRecord>();
        foreach (var file in Directory.EnumerateFiles(_root.SessionsDirectory, "*.json"))
        {
            SessionRecord? record;
            try { record = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(file), RelayJson.Indented); }
            catch (JsonException) { continue; }
            if (record is null || record.CleanShutdown || record.EndedAt is not null) continue;
            if (excludingSessionId is not null && record.SessionId == excludingSessionId) continue;
            result.Add(record);
        }
        return result;
    }
}
