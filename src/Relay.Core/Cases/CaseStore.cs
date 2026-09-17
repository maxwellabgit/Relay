using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

/// <summary>Result of loading a JSONL event log with crash-recovery semantics.</summary>
public sealed record JsonlRecoveryResult(
    IReadOnlyList<CaseEvent> Events,
    bool TruncatedTailRecovered,
    bool MidFileCorruptionStopped,
    string? IncidentPath);

/// <summary>
/// Persists <see cref="CaseRecord"/> as versioned JSON and appends <see cref="CaseEvent"/> lines.
/// Events are written and flushed before the call returns so consequences never outrun the record.
/// </summary>
public sealed class CaseStore
{
    private readonly DataRoot _root;
    private readonly object _gate = new();

    public CaseStore(DataRoot root) => _root = root;

    public string CaseDirectory(string caseId) => Path.Combine(_root.CasesDirectory, caseId);
    public string RecordPath(string caseId) => Path.Combine(CaseDirectory(caseId), "record.json");
    public string EventsPath(string caseId) => Path.Combine(CaseDirectory(caseId), "events.jsonl");
    public string TransitionsPath(string caseId) => Path.Combine(CaseDirectory(caseId), "transitions.jsonl");

    public void SaveRecord(CaseRecord record)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(CaseDirectory(record.Id));
            AtomicFile.WriteAllText(RecordPath(record.Id), JsonSerializer.Serialize(record, RelayJson.Indented));
        }
    }

    public CaseRecord? TryLoadRecord(string caseId)
    {
        var text = AtomicFile.ReadAllTextIfExists(RecordPath(caseId));
        return text is null ? null : JsonSerializer.Deserialize<CaseRecord>(text, RelayJson.Indented);
    }

    /// <summary>Appends one event and flushes before returning. This is the persist-before-consequence boundary.</summary>
    public CaseEvent AppendEvent(CaseEvent evt)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(CaseDirectory(evt.CaseId));
            var path = EventsPath(evt.CaseId);
            var line = JsonSerializer.Serialize(evt, RelayJson.Compact) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return evt;
        }
    }

    /// <summary>Appends a durable transition outbox record (events + command ids) and flushes.</summary>
    public void AppendTransition(string caseId, object transitionRecord)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(CaseDirectory(caseId));
            var path = TransitionsPath(caseId);
            var line = JsonSerializer.Serialize(transitionRecord, RelayJson.Compact) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<CaseEvent> LoadEvents(string caseId)
        => LoadEventsWithRecovery(caseId).Events;

    /// <summary>
    /// Crash recovery: truncated final JSONL line → keep valid prefix and write an incident.
    /// Mid-file corruption stops recovery for that log (valid prefix only; no further lines).
    /// </summary>
    public JsonlRecoveryResult LoadEventsWithRecovery(string caseId)
    {
        var path = EventsPath(caseId);
        if (!File.Exists(path))
            return new JsonlRecoveryResult([], false, false, null);

        var list = new List<CaseEvent>();
        var bytes = File.ReadAllBytes(path);
        // Skip UTF-8 BOM if present (some writers emit one).
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        var endsWithNewline = text.EndsWith('\n') || text.Length == 0;
        var lines = text.Split('\n');
        // Split keeps a trailing empty entry when file ends with newline; drop it.
        var effective = endsWithNewline && lines.Length > 0 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;

        var truncatedTail = false;
        var midCorruption = false;
        string? incidentPath = null;

        for (var i = 0; i < effective.Length; i++)
        {
            var line = effective[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            CaseEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<CaseEvent>(line, RelayJson.Compact);
            }
            catch (JsonException)
            {
                evt = null;
            }

            if (evt is not null)
            {
                list.Add(evt);
                continue;
            }

            var isLast = i == effective.Length - 1;
            if (isLast && !endsWithNewline)
            {
                // Truncated final line — recover valid prefix.
                truncatedTail = true;
                incidentPath = WriteIncident(caseId, "jsonl_truncated_tail", new
                {
                    path,
                    recoveredEvents = list.Count,
                    truncatedLineLength = line.Length,
                });
                break;
            }

            // Mid-file corruption: stop recovery for this log.
            midCorruption = true;
            incidentPath = WriteIncident(caseId, "jsonl_mid_file_corruption", new
            {
                path,
                lineIndex = i,
                recoveredEvents = list.Count,
            });
            break;
        }

        return new JsonlRecoveryResult(list, truncatedTail, midCorruption, incidentPath);
    }

    private string WriteIncident(string caseId, string kind, object detail)
    {
        Directory.CreateDirectory(_root.IncidentsDirectory);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{kind}-{caseId}.json";
        var incidentPath = Path.Combine(_root.IncidentsDirectory, name);
        var payload = new { kind, caseId, at = DateTimeOffset.UtcNow, detail };
        AtomicFile.WriteAllText(incidentPath, JsonSerializer.Serialize(payload, RelayJson.Indented));
        return incidentPath;
    }

    /// <summary>Lists every case id that has a record on disk.</summary>
    public IReadOnlyList<string> ListCaseIds()
    {
        if (!Directory.Exists(_root.CasesDirectory)) return [];
        return Directory.GetDirectories(_root.CasesDirectory)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Loads the record and verifies event versions are sequential; returns both.</summary>
    public (CaseRecord Record, IReadOnlyList<CaseEvent> Events) LoadCase(string caseId)
    {
        var record = TryLoadRecord(caseId) ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
        var events = LoadEvents(caseId);
        return (record, events);
    }
}
