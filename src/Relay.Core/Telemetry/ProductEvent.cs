using System.Text.Json.Serialization;

namespace Relay.Core.Telemetry;

/// <summary>
/// Bounded product telemetry event. <see cref="Properties"/> holds only scalar redacted values —
/// never arbitrary serialized objects or raw user/model text.
/// </summary>
public sealed class ProductEvent
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("monotonicMs")] public long MonotonicMs { get; init; }
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }
    [JsonPropertyName("eventName")] public required string EventName { get; init; }
    [JsonPropertyName("level")] public string Level { get; init; } = ProductEventLevels.Info;
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("caseVersion")] public long? CaseVersion { get; init; }
    [JsonPropertyName("projectId")] public string? ProjectId { get; init; }
    [JsonPropertyName("capabilityId")] public string? CapabilityId { get; init; }
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }
    [JsonPropertyName("judgmentId")] public string? JudgmentId { get; init; }
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("payloadRef")] public string? PayloadRef { get; init; }
    [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);
}

public static class ProductEventLevels
{
    public const string Debug = "debug";
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Error = "error";
}

/// <summary>Canonical event names instrumented by production RELAY.</summary>
public static class ProductEventNames
{
    public const string AppStarted = "app.started";
    public const string AppStopped = "app.stopped";
    public const string AppCrashed = "app.crashed";
    public const string UiCommandStarted = "ui.command.started";
    public const string UiCommandCompleted = "ui.command.completed";
    public const string UiCommandFailed = "ui.command.failed";
    public const string TranscriptChanged = "transcript.changed";
    public const string TranscriptWindowPersisted = "transcript.window.persisted";
    public const string QueueEnqueued = "queue.enqueued";
    public const string QueueDequeued = "queue.dequeued";
    public const string QueueIdle = "queue.idle";
    public const string CaseCreated = "case.created";
    public const string CasePhaseChanged = "case.phase_changed";
    public const string CaseCompleted = "case.completed";
    public const string CaseFailed = "case.failed";
    public const string JudgmentAuthorized = "judgment.authorized";
    public const string JudgmentBlocked = "judgment.blocked";
    public const string JudgmentDispatched = "judgment.dispatched";
    public const string JudgmentCompleted = "judgment.completed";
    public const string JudgmentDeferred = "judgment.deferred";
    public const string CapabilityStarted = "capability.started";
    public const string CapabilityCompleted = "capability.completed";
    public const string CapabilityFailed = "capability.failed";
    public const string OperationProposed = "operation.proposed";
    public const string OperationApproved = "operation.approved";
    public const string OperationExecuting = "operation.executing";
    public const string OperationCompleted = "operation.completed";
    public const string OperationFailed = "operation.failed";
    public const string FeedPublished = "feed.published";
    public const string ProblemReported = "problem.reported";
    public const string RuntimeHeartbeat = "runtime.heartbeat";

    public static readonly string[] All =
    [
        AppStarted, AppStopped, AppCrashed,
        UiCommandStarted, UiCommandCompleted, UiCommandFailed,
        TranscriptChanged, TranscriptWindowPersisted,
        QueueEnqueued, QueueDequeued, QueueIdle,
        CaseCreated, CasePhaseChanged, CaseCompleted, CaseFailed,
        JudgmentAuthorized, JudgmentBlocked, JudgmentDispatched, JudgmentCompleted, JudgmentDeferred,
        CapabilityStarted, CapabilityCompleted, CapabilityFailed,
        OperationProposed, OperationApproved, OperationExecuting, OperationCompleted, OperationFailed,
        FeedPublished, ProblemReported, RuntimeHeartbeat,
    ];
}
