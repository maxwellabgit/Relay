using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Cases;

public static class RuntimeCommandKinds
{
    public const string RetrieveContext = "RetrieveContext";
    public const string RequestJudgments = "RequestJudgments";
    public const string RequestLocalJob = "RequestLocalJob";
    public const string RequestReasoningJob = "RequestReasoningJob";
    public const string ProposeOperation = "ProposeOperation";
    public const string DispatchAuthorizedOperation = "DispatchAuthorizedOperation";
    public const string CreateOrLinkCase = "CreateOrLinkCase";
    public const string ScheduleWake = "ScheduleWake";
    public const string PublishFeedItem = "PublishFeedItem";
    public const string CompleteCase = "CompleteCase";

    public static readonly string[] All =
    [
        RetrieveContext, RequestJudgments, RequestLocalJob, RequestReasoningJob,
        ProposeOperation, DispatchAuthorizedOperation, CreateOrLinkCase,
        ScheduleWake, PublishFeedItem, CompleteCase,
    ];
}

public static class RuntimeCommandStatus
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Claimed, Completed, Failed, Cancelled];
}

/// <summary>
/// Persisted async work item with stable identity. Completion produces a case domain event.
/// Logical identity (when set) prevents two dispatchers from claiming the same work.
/// </summary>
public sealed class RuntimeCommand
{
    [JsonPropertyName("commandId")] public required string CommandId { get; init; }
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("objectiveRevision")] public long ObjectiveRevision { get; init; }
    [JsonPropertyName("logicalKey")] public string? LogicalKey { get; init; }
    [JsonPropertyName("causedByEventId")] public string? CausedByEventId { get; init; }
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }
    [JsonPropertyName("status")] public string Status { get; set; } = RuntimeCommandStatus.Pending;
    [JsonPropertyName("payload")] public Dictionary<string, JsonElement> Payload { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("claimedBy")] public string? ClaimedBy { get; set; }
    [JsonPropertyName("claimedAt")] public DateTimeOffset? ClaimedAt { get; set; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    [JsonPropertyName("resultRef")] public string? ResultRef { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
}

/// <summary>Configurable concurrency limits for async dispatch pools.</summary>
public sealed class RuntimeConcurrencyOptions
{
    /// <summary>Local generation / mind jobs. Default 1.</summary>
    public int LocalGeneration { get; set; } = 1;
    /// <summary>Concurrent Jev judgment requests. Default 4.</summary>
    public int Jev { get; set; } = 4;
    /// <summary>Concurrent search / fetch / delegate work. Default 4.</summary>
    public int SearchFetchDelegate { get; set; } = 4;
}
