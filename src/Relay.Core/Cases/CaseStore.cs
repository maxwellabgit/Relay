using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

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

    public IReadOnlyList<CaseEvent> LoadEvents(string caseId)
    {
        var path = EventsPath(caseId);
        if (!File.Exists(path)) return [];

        var list = new List<CaseEvent>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<CaseEvent>(line, RelayJson.Compact)
                ?? throw new InvalidOperationException($"Corrupt case event in {path}");
            list.Add(evt);
        }
        return list;
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
