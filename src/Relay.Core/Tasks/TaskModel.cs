using System.Text.Json.Serialization;

namespace Relay.Core.Tasks;

/// <summary>Where a task came from. Origin decides authorization, urgency, and how the result is shown.</summary>
public enum TaskOrigin
{
    /// <summary>The user asked (command chord or the ask box). An answer is owed.</summary>
    Direct,
    /// <summary>RELAY0 noticed something in an enabled stream. May stay silent.</summary>
    Observed,
    /// <summary>A follow-up to an earlier task (an external result returned, a proposal was edited). Inherits the parent's scope.</summary>
    Dialogue,
}

/// <summary>What sort of work a task is. The kind names the lane and the permissions the lane may use.</summary>
public enum TaskKind
{
    Remember,
    Check,
    Resolve,
    Answer,
    Organize,
    Research,
    Improve,
}

/// <summary>How much of the user's attention a finished task may take. Chosen by the arbiter, never by the model alone.</summary>
public enum Presentation
{
    None,
    Ambient,
    Result,
    Alert,
    Proposal,
    Findings,
}

public enum TaskStatus
{
    Planning,
    AwaitingApproval,
    Executing,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// What is needed to start a task. For a direct ask the text is the instruction verbatim; for an
/// observed task it is the judge's focused prompt, and the excerpt ids point at the retained words.
/// </summary>
public sealed record TaskSeed(
    TaskOrigin Origin,
    TaskKind Kind,
    string Text,
    string SourceEventId,
    string? CaptureId = null,
    string? ExcerptId = null,
    IReadOnlyList<string>? SegmentIds = null,
    string? Summary = null,
    string? Why = null,
    double Confidence = 1.0,
    string? Topic = null,
    string? ProjectHint = null,
    Presentation? SuggestedPresentation = null,
    string? ParentTaskId = null,
    string? MergeKey = null,
    string? NoteText = null,
    string? NoteType = null);

/// <summary>
/// The two-axis gap statement a planner makes before delegating: which facts have no local source,
/// and whether the local model itself is the limit. A missing fact may be resolved by a local lookup;
/// only a capability gap justifies external work.
/// </summary>
public sealed record KnowledgeState(
    [property: JsonPropertyName("known")] IReadOnlyList<string> Known,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing,
    [property: JsonPropertyName("capabilityGap")] bool CapabilityGap,
    [property: JsonPropertyName("summary")] string Summary)
{
    public static readonly KnowledgeState Empty = new([], [], false, "");
    public bool IsEmpty => Known.Count == 0 && Missing.Count == 0 && !CapabilityGap && Summary.Length == 0;
}

/// <summary>One tool call inside a task, as recorded for diagnostics.</summary>
public sealed record ToolCallRecord(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Args,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("items")] int Items,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>One model round trip inside a task: sizes, tokens, timing — never the prompt text.</summary>
public sealed record ModelCallRecord(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("promptChars")] int PromptChars,
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("completionTokens")] int CompletionTokens,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>A proposal as it stood inside the task, with what policy said and what happened.</summary>
public sealed record ProposalRecord(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] IReadOnlyDictionary<string, string> Target,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string> DependsOn,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("grantedBy")] string? GrantedBy);

/// <summary>
/// The post-hoc record of one task: what RELAY0 keyed on, what it did, what it cost, what the user
/// saw and did. Written once when the task ends; the ledger holds the same facts as events.
/// </summary>
public sealed class TaskDiagnostics
{
    [JsonPropertyName("taskId")] public required string TaskId { get; init; }
    [JsonPropertyName("origin")] public required string Origin { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("focusedPrompt")] public required string FocusedPrompt { get; init; }
    [JsonPropertyName("sourceEventId")] public required string SourceEventId { get; init; }
    [JsonPropertyName("excerptId")] public string? ExcerptId { get; init; }
    [JsonPropertyName("parentTaskId")] public string? ParentTaskId { get; init; }
    [JsonPropertyName("planner")] public string? Planner { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("steps")] public IReadOnlyList<string> Steps { get; init; } = [];
    [JsonPropertyName("answer")] public string? Answer { get; init; }
    [JsonPropertyName("citations")] public IReadOnlyList<string> Citations { get; init; } = [];
    [JsonPropertyName("knowledge")] public KnowledgeState Knowledge { get; init; } = KnowledgeState.Empty;
    [JsonPropertyName("toolCalls")] public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = [];
    [JsonPropertyName("modelCalls")] public IReadOnlyList<ModelCallRecord> ModelCalls { get; init; } = [];
    [JsonPropertyName("proposals")] public IReadOnlyList<ProposalRecord> Proposals { get; init; } = [];
    [JsonPropertyName("presentation")] public required string Presentation { get; init; }
    [JsonPropertyName("presentationReason")] public string? PresentationReason { get; init; }
    [JsonPropertyName("userResponse")] public string? UserResponse { get; init; }
    [JsonPropertyName("promptTokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("completionTokens")] public int CompletionTokens { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("wallMs")] public long WallMs { get; init; }
}

public static class TaskLanes
{
    /// <summary>The process tag shown for a task in a given status. Each tag names the only permissions that lane may use.</summary>
    public static string Tag(TaskKind kind, TaskStatus status, bool externalPending = false) => status switch
    {
        TaskStatus.Planning => externalPending ? "Asking model" : kind switch
        {
            TaskKind.Remember => "Extracting",
            TaskKind.Check or TaskKind.Answer or TaskKind.Resolve => "Recalling",
            TaskKind.Organize => "Planning",
            TaskKind.Research => "Planning",
            TaskKind.Improve => "Planning",
            _ => "Planning",
        },
        TaskStatus.AwaitingApproval => "Awaiting Approval",
        TaskStatus.Executing => kind switch
        {
            TaskKind.Organize => "Organizing",
            TaskKind.Improve => "Applying",
            TaskKind.Research => "Asking model",
            _ => "Applying",
        },
        TaskStatus.Completed => "Completed",
        TaskStatus.Failed => "Failed",
        TaskStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    public static string Wire(this TaskOrigin origin) => origin.ToString().ToLowerInvariant();
    public static string Wire(this TaskKind kind) => kind.ToString().ToLowerInvariant();
    public static string Wire(this Presentation presentation) => presentation.ToString().ToLowerInvariant();
    public static string Wire(this TaskStatus status) => status switch
    {
        TaskStatus.AwaitingApproval => "awaiting_approval",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static TaskKind ParseKind(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "remember" or "note" => TaskKind.Remember,
        "check" or "verify" => TaskKind.Check,
        "resolve" or "define" or "lookup" => TaskKind.Resolve,
        "answer" or "question" => TaskKind.Answer,
        "organize" or "organise" or "transform" => TaskKind.Organize,
        "research" or "delegate" => TaskKind.Research,
        "improve" or "improvement" or "preference" => TaskKind.Improve,
        _ => TaskKind.Answer,
    };

    public static Presentation? ParsePresentation(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "none" or "silent" => Presentation.None,
        "ambient" => Presentation.Ambient,
        "result" => Presentation.Result,
        "alert" => Presentation.Alert,
        "proposal" => Presentation.Proposal,
        "findings" => Presentation.Findings,
        _ => null,
    };
}
