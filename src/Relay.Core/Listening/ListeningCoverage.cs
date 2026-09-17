using System.Text.Json.Serialization;

namespace Relay.Core.Listening;

/// <summary>Segment range covered by a listening window (primary talk or preceding context).</summary>
public sealed class ListeningCoverage
{
    [JsonPropertyName("segmentIds")] public List<string> SegmentIds { get; set; } = [];
    [JsonPropertyName("startSequence")] public int StartSequence { get; set; }
    [JsonPropertyName("endSequence")] public int EndSequence { get; set; }
    [JsonPropertyName("startTs")] public DateTimeOffset? StartTs { get; set; }
    [JsonPropertyName("endTs")] public DateTimeOffset? EndTs { get; set; }
    [JsonPropertyName("charCount")] public int CharCount { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = CoverageRoles.Primary;

    public bool ContainsSegment(string segmentId)
        => SegmentIds.Contains(segmentId, StringComparer.Ordinal);

    public bool Overlaps(ListeningCoverage other)
        => SegmentIds.Intersect(other.SegmentIds, StringComparer.Ordinal).Any();
}

public static class CoverageRoles
{
    public const string Primary = "primary";
    public const string Context = "context";
}

public static class ListeningWindowStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Deferred = "deferred";
    public const string Failed = "failed";
}

/// <summary>Hard policy bounds for durable listening windows.</summary>
public static class ListeningWindowPolicy
{
    public static readonly TimeSpan CoalesceGap = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxPrimarySpan = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxPrecedingOverlap = TimeSpan.FromSeconds(60);
    public const int MaxWindowChars = 8000;
    /// <summary>Never reduce durable backlog to a fixed recent-segment window.</summary>
    public const int ForbiddenRecentOnlyBacklogLimit = 32;
}
