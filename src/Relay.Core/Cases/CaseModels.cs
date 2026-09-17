using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Cases;

/// <summary>Where a case came from. Origin changes authorization and urgency, not the pipeline.</summary>
public static class CaseOrigin
{
    public const string Direct = "direct";
    public const string Observed = "observed";
    public const string Dialogue = "dialogue";

    public static readonly string[] All = [Direct, Observed, Dialogue];
}

/// <summary>What kind of work the case is. Improvement is a kind, not an origin.</summary>
public static class CaseKind
{
    public const string Remember = "remember";
    public const string Check = "check";
    public const string Resolve = "resolve";
    public const string Answer = "answer";
    public const string Organize = "organize";
    public const string Research = "research";
    public const string Improve = "improve";

    public static readonly string[] All = [Remember, Check, Resolve, Answer, Organize, Research, Improve];
}

public static class CaseStatus
{
    public const string Active = "active";
    public const string Waiting = "waiting";
    public const string Suspended = "suspended";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Active, Waiting, Suspended, Completed, Cancelled];
}

/// <summary>
/// One durable unit of work. The mind steps cases; origin and kind only change allowed moves and presentation.
/// </summary>
public sealed class CaseRecord
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public long Version { get; set; }
    [JsonPropertyName("origin")] public required string Origin { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("approvedObjective")] public string? ApprovedObjective { get; set; }
    [JsonPropertyName("sourceRefs")] public List<string> SourceRefs { get; set; } = [];
    [JsonPropertyName("allowedCapabilities")] public List<string> AllowedCapabilities { get; set; } = [];
    [JsonPropertyName("budgets")] public CaseBudgets Budgets { get; set; } = new();
    [JsonPropertyName("pendingWaits")] public List<string> PendingWaits { get; set; } = [];
    [JsonPropertyName("pendingOperationIds")] public List<string> PendingOperationIds { get; set; } = [];
    [JsonPropertyName("processedEventIds")] public List<string> ProcessedEventIds { get; set; } = [];
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("completionCriteria")] public string? CompletionCriteria { get; set; }
    [JsonPropertyName("presentationPolicy")] public string? PresentationPolicy { get; set; }
    [JsonPropertyName("parentCaseId")] public string? ParentCaseId { get; set; }
    [JsonPropertyName("childCaseIds")] public List<string> ChildCaseIds { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = CaseStatus.Active;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CaseBudgets
{
    [JsonPropertyName("maxSteps")] public int MaxSteps { get; set; } = 32;
    [JsonPropertyName("stepsUsed")] public int StepsUsed { get; set; }
    [JsonPropertyName("maxTokens")] public int MaxTokens { get; set; }
    [JsonPropertyName("tokensUsed")] public int TokensUsed { get; set; }
}

/// <summary>One ordered event within a case. Persisted before any consequence is dispatched.</summary>
public sealed class CaseEvent
{
    [JsonPropertyName("eventId")] public required string EventId { get; init; }
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("caseVersionAfter")] public long CaseVersionAfter { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; init; }
    [JsonPropertyName("causationId")] public string? CausationId { get; init; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; init; }
}

public static class CaseEventTypes
{
    public const string UserInput = "user.input";
    public const string MindStepped = "mind.stepped";
    public const string ToolCalled = "tool.called";
    public const string ToolResult = "tool.result";
    public const string SegmentIngested = "stream.segment_ingested";
    public const string ListeningStarted = "stream.listening_started";
    public const string ListeningStopped = "stream.listening_stopped";
    public const string TaskRaised = "case.task_raised";
    public const string OperationProposed = "operation.proposed";
    public const string OperationApproved = "operation.approved";
    public const string OperationDenied = "operation.denied";
    public const string OperationEdited = "operation.edited";
    public const string OperationExecuted = "operation.executed";
    public const string OperationCompleted = "operation.completed";
    public const string OperationFailed = "operation.failed";
    public const string CaseSuspended = "case.suspended";
    public const string CaseResumed = "case.resumed";
    public const string CaseCompleted = "case.completed";
    public const string CaseCancelled = "case.cancelled";
    public const string WaitEntered = "wait.entered";
    public const string DuplicateIgnored = "operation.duplicate_ignored";
    public const string MoveRejected = "move.rejected";
}

/// <summary>Content-addressed object reference returned by <see cref="ObjectStore"/>.</summary>
public sealed record StoredObject(string ObjectId, string Sha256, string Path);
