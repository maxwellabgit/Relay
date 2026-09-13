using System.Text.Json.Serialization;

namespace Relay.Core.Tasks;

/// <summary>
/// The on-disk shape of <c>tasks\{id}.live.json</c>. Existing field names are stable; new fields are additive
/// so a crash can resume waiting work instead of only renaming the file to <c>.interrupted.json</c>.
/// </summary>
public sealed class DurableTaskRecord
{
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("lane")] public string? Lane { get; init; }
    [JsonPropertyName("captureId")] public string? CaptureId { get; init; }
    [JsonPropertyName("sourceEventId")] public string? SourceEventId { get; init; }
    [JsonPropertyName("instructionChars")] public int InstructionChars { get; init; }
    [JsonPropertyName("stage")] public string? Stage { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("proposals")] public IReadOnlyList<DurableProposalRef>? Proposals { get; init; }

    // --- Additive fields (Step 2) ---

    /// <summary>Full instruction text (today only <see cref="InstructionChars"/> was kept).</summary>
    [JsonPropertyName("instruction")] public string? Instruction { get; init; }
    /// <summary>Same objective as <see cref="Instruction"/> when the task was raised with one; kept as a named alias for resume.</summary>
    [JsonPropertyName("objective")] public string? Objective { get; init; }
    [JsonPropertyName("waitingFor")] public string? WaitingFor { get; init; }
    [JsonPropertyName("pendingProposalId")] public string? PendingProposalId { get; init; }
    [JsonPropertyName("planSummary")] public string? PlanSummary { get; init; }
    [JsonPropertyName("planSteps")] public IReadOnlyList<string>? PlanSteps { get; init; }
    [JsonPropertyName("planAnswer")] public string? PlanAnswer { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("appliedEventIds")] public IReadOnlyList<string>? AppliedEventIds { get; init; }
    [JsonPropertyName("capabilityIds")] public IReadOnlyList<string>? CapabilityIds { get; init; }
    [JsonPropertyName("loopOrigin")] public string? LoopOrigin { get; init; }
    [JsonPropertyName("foreground")] public bool Foreground { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("parentTaskId")] public string? ParentTaskId { get; init; }
    [JsonPropertyName("excerptId")] public string? ExcerptId { get; init; }
}

public sealed class DurableProposalRef
{
    [JsonPropertyName("proposalId")] public string? ProposalId { get; init; }
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
}
