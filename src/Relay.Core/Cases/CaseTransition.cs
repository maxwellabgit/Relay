using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Cases;

/// <summary>
/// Deterministic case controller contract. <see cref="Handle"/> must not perform HTTP,
/// model inference, file mutation, or tool execution — only produce a transition.
/// </summary>
public interface ICaseController
{
    string ControllerId { get; }
    string ControllerVersion { get; }
    CaseTransition Handle(CaseSnapshot snapshot, CaseInput input);
}

/// <summary>Immutable view of case state presented to a controller.</summary>
public sealed class CaseSnapshot
{
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("origin")] public required string Origin { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = CasePurpose.Objective;
    [JsonPropertyName("authorizationStatus")] public string AuthorizationStatus { get; init; } = CaseAuthorizationStatus.None;
    [JsonPropertyName("objectiveRevision")] public long ObjectiveRevision { get; init; }
    [JsonPropertyName("approvedObjective")] public string? ApprovedObjective { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("stage")] public string? Stage { get; init; }
    [JsonPropertyName("waitingReason")] public string? WaitingReason { get; init; }
    [JsonPropertyName("controllerId")] public string? ControllerId { get; init; }
    [JsonPropertyName("controllerVersion")] public string? ControllerVersion { get; init; }
    [JsonPropertyName("pendingCommandIds")] public IReadOnlyList<string> PendingCommandIds { get; init; } = [];
    [JsonPropertyName("pendingOperationIds")] public IReadOnlyList<string> PendingOperationIds { get; init; } = [];
    [JsonPropertyName("pendingWaits")] public IReadOnlyList<string> PendingWaits { get; init; } = [];
    [JsonPropertyName("processedEventIds")] public IReadOnlyList<string> ProcessedEventIds { get; init; } = [];
    [JsonPropertyName("decisionDependencyRefs")] public IReadOnlyList<string> DecisionDependencyRefs { get; init; } = [];
    [JsonPropertyName("completionCriteria")] public string? CompletionCriteria { get; init; }
    [JsonPropertyName("unresolvedConflictIds")] public IReadOnlyList<string> UnresolvedConflictIds { get; init; } = [];
    [JsonPropertyName("sourceRefs")] public IReadOnlyList<string> SourceRefs { get; init; } = [];
    [JsonPropertyName("allowedCapabilities")] public IReadOnlyList<string> AllowedCapabilities { get; init; } = [];
    [JsonPropertyName("budgets")] public CaseBudgets Budgets { get; init; } = new();
    [JsonPropertyName("parentCaseId")] public string? ParentCaseId { get; init; }
    [JsonPropertyName("childCaseIds")] public IReadOnlyList<string> ChildCaseIds { get; init; } = [];
    [JsonPropertyName("presentationPolicy")] public string? PresentationPolicy { get; init; }
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("recentEvents")] public IReadOnlyList<CaseEvent> RecentEvents { get; init; } = [];
    [JsonPropertyName("pendingOperations")] public IReadOnlyList<OperationEnvelope> PendingOperations { get; init; } = [];
    [JsonPropertyName("recentSegments")] public IReadOnlyList<ListeningSegmentView> RecentSegments { get; init; } = [];
    [JsonPropertyName("availableTools")] public IReadOnlyList<string> AvailableTools { get; init; } = [];
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    [JsonPropertyName("stepsUsed")] public int StepsUsed { get; init; }
}

/// <summary>Input that triggers one controller transition.</summary>
public sealed class CaseInput
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("eventId")] public string? EventId { get; init; }
    [JsonPropertyName("commandId")] public string? CommandId { get; init; }
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }

    public const string Wake = "wake";
    public const string Event = "event";
    public const string CommandCompleted = "command_completed";
    public const string OperationResult = "operation_result";
    public const string ScheduledRetry = "scheduled_retry";
    public const string ManualStep = "manual_step";
}

/// <summary>One domain event produced by a controller (applied by <see cref="CaseReducer"/>).</summary>
public sealed class CaseDomainEvent
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; init; }
    [JsonPropertyName("causationId")] public string? CausationId { get; init; }
}

/// <summary>
/// Deterministic transition: domain events plus async commands for the outbox.
/// Commands are persisted before dispatch and carry stable identities.
/// </summary>
public sealed class CaseTransition
{
    [JsonPropertyName("events")] public IReadOnlyList<CaseDomainEvent> Events { get; init; } = [];
    [JsonPropertyName("commands")] public IReadOnlyList<RuntimeCommand> Commands { get; init; } = [];
    [JsonPropertyName("feed")] public string? Feed { get; init; }
    [JsonPropertyName("feedLevel")] public string? FeedLevel { get; init; }
    [JsonPropertyName("skip")] public bool Skip { get; init; }
    [JsonPropertyName("skipReason")] public string? SkipReason { get; init; }
}

public static class CasePurpose
{
    public const string Observation = "observation";
    public const string UnresolvedQuestion = "unresolved_question";
    public const string Objective = "objective";
    public const string Improvement = "improvement";

    public static readonly string[] All = [Observation, UnresolvedQuestion, Objective, Improvement];
}

public static class CaseAuthorizationStatus
{
    public const string None = "none";
    public const string Proposed = "proposed";
    public const string Authorized = "authorized";
    public const string Revoked = "revoked";

    public static readonly string[] All = [None, Proposed, Authorized, Revoked];
}

public static class CaseDomainEventTypes
{
    public const string MindStepped = "mind.stepped";
    public const string WaitEntered = "wait.entered";
    public const string CaseCompleted = "case.completed";
    public const string CaseCancelled = "case.cancelled";
    public const string MoveRejected = "move.rejected";
    public const string StatusChanged = "case.status_changed";
    public const string ObjectiveRevised = "case.objective_revised";
    public const string DecisionDepsSet = "case.decision_deps_set";
    public const string CitationsApplied = "case.citations_applied";
    public const string ToolCalled = "tool.called";
    public const string ToolResult = "tool.result";
    public const string TaskRaised = "case.task_raised";
    public const string OperationProposed = "operation.proposed";
    public const string SegmentHandled = "stream.segment_handled";
    public const string CommandCompleted = "command.completed";
    public const string CommandFailed = "command.failed";
    public const string LateResultAccepted = "operation.late_result_accepted";
    public const string DuplicateIgnored = "operation.duplicate_ignored";
}
