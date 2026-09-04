using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Memory;

public sealed record RoutingDecisionRecord(
    [property: JsonPropertyName("noteId")] string NoteId,
    [property: JsonPropertyName("captureId")] string CaptureId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("candidates")] IReadOnlyList<RoutingCandidate> Candidates);

public sealed record DisputeRecord(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectSlug")] string ProjectSlug,
    [property: JsonPropertyName("newNoteId")] string NewNoteId,
    [property: JsonPropertyName("existingNoteId")] string ExistingNoteId,
    [property: JsonPropertyName("newText")] string NewText,
    [property: JsonPropertyName("existingText")] string ExistingText,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>
/// Decisions waiting for the user survive restarts as small files under <c>staging\review</c>.
/// Resolving one moves its file to <c>resolved\</c>; nothing is deleted.
/// </summary>
public sealed class ReviewStore
{
    private readonly DataRoot _root;

    public ReviewStore(DataRoot root) => _root = root;

    private string RoutingDir => Path.Combine(_root.ReviewDirectory, "routing");
    private string DisputesDir => Path.Combine(_root.ReviewDirectory, "disputes");

    public void SaveRouting(RoutingDecisionRecord record) => Write(Path.Combine(RoutingDir, record.NoteId + ".json"), record);
    public void SaveDispute(DisputeRecord record) => Write(Path.Combine(DisputesDir, record.NewNoteId + ".json"), record);

    public IReadOnlyList<RoutingDecisionRecord> PendingRouting() => ReadAll<RoutingDecisionRecord>(RoutingDir);
    public IReadOnlyList<DisputeRecord> PendingDisputes() => ReadAll<DisputeRecord>(DisputesDir);

    public RoutingDecisionRecord? Routing(string noteId) => PendingRouting().FirstOrDefault(r => r.NoteId == noteId);
    public DisputeRecord? Dispute(string newNoteId) => PendingDisputes().FirstOrDefault(d => d.NewNoteId == newNoteId);

    public bool ResolveRouting(string noteId, string resolution) => Resolve(RoutingDir, noteId, resolution);
    public bool ResolveDispute(string newNoteId, string resolution) => Resolve(DisputesDir, newNoteId, resolution);

    private static bool Resolve(string dir, string id, string resolution)
    {
        var path = Path.Combine(dir, id + ".json");
        if (!File.Exists(path)) return false;
        var resolved = Path.Combine(dir, "resolved");
        Directory.CreateDirectory(resolved);
        File.Move(path, Path.Combine(resolved, $"{id}.{resolution}.json"), overwrite: true);
        return true;
    }

    private static void Write<T>(string path, T record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, RelayJson.Indented));
    }

    private static IReadOnlyList<T> ReadAll<T>(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<T>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(File.ReadAllText(file), RelayJson.Indented);
                if (item is not null) list.Add(item);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }
}
