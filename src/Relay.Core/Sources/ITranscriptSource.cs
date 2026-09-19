namespace Relay.Core.Sources;

public sealed record TranscriptSegment(
    string SegmentId,
    DateTimeOffset Timestamp,
    string? Speaker,
    string Text,
    bool IsFinal,
    string? Cursor);

/// <summary>
/// Speech-to-text is owned by the adapter. RELAY consumes transcription.
/// Only final segments enter the durable source pipeline.
/// </summary>
public interface ITranscriptSource : IAsyncDisposable
{
    string SourceId { get; }
    IAsyncEnumerable<TranscriptSegment> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exact, validated reference into a persisted object. Offsets are inclusive-exclusive UTF-16 indices
/// when <see cref="OffsetsValidated"/> is true.
/// </summary>
public sealed record SourceSliceRef(
    string ObjectId,
    string ObjectVersion,
    string Sha256,
    string? SourceEventId,
    string? SegmentId,
    string? ProviderItemId,
    int Start,
    int End,
    bool OffsetsValidated,
    Security.DataClassification Classification);
