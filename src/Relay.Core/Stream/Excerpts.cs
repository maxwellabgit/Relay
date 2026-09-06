using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Judge;
using Relay.Core.Storage;

namespace Relay.Core.Stream;

/// <summary>A segment as retained inside an excerpt; the excerpt is the only place stream text outlives the buffer.</summary>
public sealed record ExcerptSegment(
    [property: JsonPropertyName("segmentId")] string SegmentId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("speaker")] string? Speaker);

/// <summary>
/// The words selected for one task: the trigger segment and the segments the judge said substantiate
/// it, bounded by the retention guard. Segments already held by an earlier excerpt are not copied;
/// <see cref="References"/> points at the excerpt that has them. Offsets inside <see cref="Text"/> are
/// what note spans cite.
/// </summary>
public sealed class Excerpt
{
    [JsonPropertyName("excerptId")] public required string ExcerptId { get; init; }
    [JsonPropertyName("streamId")] public required string StreamId { get; init; }
    [JsonPropertyName("triggerSegmentId")] public required string TriggerSegmentId { get; init; }
    [JsonPropertyName("selectedBy")] public required string SelectedBy { get; init; }   // judge name
    [JsonPropertyName("reason")] public required string Reason { get; init; }           // judge summary
    [JsonPropertyName("segments")] public required IReadOnlyList<ExcerptSegment> Segments { get; init; }
    [JsonPropertyName("references")] public IReadOnlyList<ExcerptReference> References { get; init; } = [];
    [JsonPropertyName("from")] public required DateTimeOffset From { get; init; }
    [JsonPropertyName("to")] public required DateTimeOffset To { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("shrunkByGuard")] public bool ShrunkByGuard { get; init; }

    [JsonIgnore] public string Text => string.Join(" ", Segments.Select(s => s.Text));
    [JsonIgnore] public double Seconds => Segments.Count == 0 ? 0 : Math.Max(1, (To - From).TotalSeconds);

    /// <summary>Character range of a segment inside <see cref="Text"/>, for note spans.</summary>
    public (int Start, int End)? RangeOf(string segmentId)
    {
        var offset = 0;
        foreach (var s in Segments)
        {
            if (s.SegmentId == segmentId) return (offset, offset + s.Text.Length);
            offset += s.Text.Length + 1;
        }
        return null;
    }
}

/// <summary>Segments held by an earlier excerpt instead of being copied again.</summary>
public sealed record ExcerptReference(
    [property: JsonPropertyName("excerptId")] string ExcerptId,
    [property: JsonPropertyName("segmentIds")] IReadOnlyList<string> SegmentIds);

public sealed record RetentionDecision(bool Allowed, bool Shrunk, string Reason);

/// <summary>
/// Keeps trigger-anchored excerpts from adding up to the conversation. Two limits: one excerpt may
/// hold at most <see cref="MaxExcerptSeconds"/> of talk, and the total retained across a stream may
/// not exceed <see cref="MaxRetainedFraction"/> of the time elapsed; beyond that, excerpts shrink to
/// the trigger sentence alone.
/// </summary>
public sealed class RetentionGuard
{
    public RetentionGuard(double maxExcerptSeconds = 30, double maxRetainedFraction = 0.25)
    {
        MaxExcerptSeconds = maxExcerptSeconds;
        MaxRetainedFraction = maxRetainedFraction;
    }

    public double MaxExcerptSeconds { get; }
    public double MaxRetainedFraction { get; }

    public RetentionDecision Decide(double requestedSeconds, double retainedSecondsSoFar, double elapsedSeconds)
    {
        if (requestedSeconds > MaxExcerptSeconds) return new RetentionDecision(true, true, $"excerpt of {requestedSeconds:0}s exceeds the {MaxExcerptSeconds:0}s bound");
        // The first minute of a stream is exempt from the fraction rule; otherwise the first finding could never keep context.
        if (elapsedSeconds >= 60 && (retainedSecondsSoFar + requestedSeconds) / elapsedSeconds > MaxRetainedFraction)
            return new RetentionDecision(true, true, $"retained {retainedSecondsSoFar:0}s of {elapsedSeconds:0}s would pass {MaxRetainedFraction:P0}");
        return new RetentionDecision(true, false, "within bounds");
    }
}

/// <summary>
/// Builds and persists excerpts. Overlap is by reference: a segment already kept by an earlier excerpt of
/// the same stream is linked, not copied, so two adjacent findings cannot double the retained text.
/// </summary>
public sealed class ExcerptStore
{
    private readonly DataRoot _root;
    private readonly Dictionary<string, string> _segmentOwner = new(StringComparer.Ordinal); // segmentId → excerptId (this process)

    public ExcerptStore(DataRoot root) => _root = root;

    public double RetainedSeconds { get; private set; }
    public int Count { get; private set; }

    public Excerpt Build(string streamId, JudgeFinding finding, string judgeName, ConversationBuffer buffer, RetentionGuard guard, double elapsedSeconds, DateTimeOffset now)
    {
        var wanted = finding.SegmentIds.Select(buffer.Find).Where(s => s is not null).Select(s => s!).OrderBy(s => s.At).ToList();
        if (wanted.Count == 0) throw new InvalidOperationException("The finding names no segment still in the buffer.");
        var trigger = wanted[^1];
        var seconds = Math.Max(1, (wanted[^1].At - wanted[0].At).TotalSeconds);
        var decision = guard.Decide(seconds, RetainedSeconds, elapsedSeconds);
        if (decision.Shrunk) wanted = [trigger];

        var copy = new List<ExcerptSegment>();
        var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in wanted)
        {
            if (_segmentOwner.TryGetValue(s.SegmentId, out var owner))
            {
                if (!references.TryGetValue(owner, out var list)) references[owner] = list = new List<string>();
                list.Add(s.SegmentId);
            }
            else copy.Add(new ExcerptSegment(s.SegmentId, s.At, s.Text, s.Speaker));
        }
        var excerpt = new Excerpt
        {
            ExcerptId = Ulid.NewUlid(now),
            StreamId = streamId,
            TriggerSegmentId = trigger.SegmentId,
            SelectedBy = judgeName,
            Reason = finding.Summary,
            Segments = copy,
            References = references.Select(kv => new ExcerptReference(kv.Key, kv.Value)).ToList(),
            From = wanted[0].At,
            To = wanted[^1].At,
            CreatedAt = now,
            ShrunkByGuard = decision.Shrunk,
        };
        return excerpt;
    }

    public string Persist(Excerpt excerpt)
    {
        Directory.CreateDirectory(_root.ExcerptsDirectory);
        var path = Path.Combine(_root.ExcerptsDirectory, excerpt.ExcerptId + ".json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(excerpt, RelayJson.Indented));
        foreach (var s in excerpt.Segments) _segmentOwner[s.SegmentId] = excerpt.ExcerptId;
        RetainedSeconds += excerpt.Segments.Count == 0 ? 0 : Math.Max(1, (excerpt.Segments[^1].At - excerpt.Segments[0].At).TotalSeconds);
        Count++;
        return path;
    }

    public Excerpt? Read(string excerptId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(_root.ExcerptsDirectory, excerptId + ".json"));
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<Excerpt>(text, RelayJson.Indented); } catch (JsonException) { return null; }
    }

    public IReadOnlyList<Excerpt> All()
    {
        if (!Directory.Exists(_root.ExcerptsDirectory)) return [];
        var list = new List<Excerpt>();
        foreach (var file in Directory.EnumerateFiles(_root.ExcerptsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var e = JsonSerializer.Deserialize<Excerpt>(File.ReadAllText(file), RelayJson.Indented);
                if (e is not null) list.Add(e);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }

    /// <summary>Total characters of stream text held on disk across all excerpts — what a retention test measures.</summary>
    public int RetainedChars() => All().Sum(e => e.Segments.Sum(s => s.Text.Length));

    /// <summary>A new stream starts with no overlap history; per-stream retention resets.</summary>
    public void BeginStream()
    {
        _segmentOwner.Clear();
        RetainedSeconds = 0;
    }
}
