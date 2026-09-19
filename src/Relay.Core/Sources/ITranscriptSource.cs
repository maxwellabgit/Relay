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

public sealed record SourceSliceRef(string ObjectId, string Sha256, int Start, int End, string Classification);
