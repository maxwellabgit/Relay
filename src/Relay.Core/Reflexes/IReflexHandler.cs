using System.Text.Json;
using Relay.Core.Artifacts;
using Relay.Core.Connectors;
using Relay.Core.Sources;

namespace Relay.Core.Reflexes;

public enum ApprovalMode
{
    AlwaysAsk,
    StandingGrantEligible,
}

public enum ReflexActivationState
{
    Inactive,
    Active,
    Paused,
}

public sealed record ReflexBudgets(
    int MaxSourceAttempts,
    int MaxJudgmentRounds,
    int MaxHostedTokens);

public sealed record ReflexRetryPolicy(
    int MaxAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff);

public enum RollbackStrategy
{
    None,
    CompensatingAction,
    SoftDisable,
}

/// <summary>Explicit rollback policy. A boolean is not enough.</summary>
public sealed record ReflexRollbackPolicy(
    RollbackStrategy Strategy,
    ConnectorActionRef? CompensatingAction = null,
    string? Notes = null);

/// <summary>Runtime outcome counters. Not part of the immutable definition.</summary>
public sealed record ReflexRunSummary(
    int RunCount,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? LastRunAt);

/// <summary>Mutable activation/outcome state for one Reflex version.</summary>
public sealed record ReflexState(
    ReflexRef Reflex,
    long StateVersion,
    ReflexActivationState Activation,
    ReflexRunSummary Runs);

/// <summary>
/// Versioned declarative automation. Policy lives here, not inside handler code.
/// Definitions are immutable and version-addressable.
/// </summary>
public sealed record ReflexDefinition(
    string Id,
    int Version,
    string DisplayName,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> NegativeTriggers,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<ConnectorRef> PermittedSources,
    IReadOnlyList<ConnectorActionRef> ReadPlan,
    IReadOnlyList<JudgmentDefinitionRef> Judgments,
    IReadOnlyList<ConnectorActionRef> PermittedWriteActions,
    ApprovalMode ApprovalMode,
    ReflexBudgets Budgets,
    ReflexRetryPolicy RetryPolicy,
    IReadOnlyList<string> EvaluationFixtureIds,
    string ExplanationTemplate,
    ReflexActivationState DefaultActivation,
    ReflexRollbackPolicy Rollback)
{
    public ReflexRef Ref => new(Id, Version);
}

public sealed record ReflexContext(
    string CaseId,
    long CaseVersion,
    ReflexRef Reflex,
    IReadOnlyList<SourceSliceRef> TriggerSourceRefs,
    IReadOnlyList<Connection> EligibleConnections,
    ReflexBudgets RemainingBudgets,
    DateTimeOffset Now);

public sealed record ReflexEvidenceDraft(
    string Kind,
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs);

/// <summary>
/// Reflex-proposed write intent. The operation broker canonicalizes arguments,
/// resolves granted scope from policy, and calculates hash / idempotency key.
/// </summary>
public sealed record OperationProposal(
    string ConnectionId,
    ConnectorActionRef Action,
    JsonElement Arguments,
    IReadOnlyList<SourceSliceRef> InputRefs,
    JsonElement RequestedResourceScope,
    IReadOnlyList<OperationPrecondition> Preconditions,
    string? SuggestedIdempotencyKey = null);

/// <summary>Closed Reflex result hierarchy. Invalid Kind/field combinations are unrepresentable.</summary>
public abstract record ReflexResult(string Summary, IReadOnlyList<SourceSliceRef> SourceRefs, IReadOnlyList<string> JudgmentIds);

public sealed record FindingResult(
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds,
    IReadOnlyList<ReflexEvidenceDraft> EvidenceDrafts) : ReflexResult(Summary, SourceRefs, JudgmentIds);

public sealed record ReadRequestedResult(
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds,
    IReadOnlyList<ReadRequest> ReadRequests) : ReflexResult(Summary, SourceRefs, JudgmentIds);

public sealed record OperationProposedResult(
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds,
    IReadOnlyList<OperationProposal> OperationProposals) : ReflexResult(Summary, SourceRefs, JudgmentIds);

public sealed record ClarificationRequiredResult(
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds,
    string ClarificationPrompt) : ReflexResult(Summary, SourceRefs, JudgmentIds);

public sealed record NoActionResult(
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds) : ReflexResult(Summary, SourceRefs, JudgmentIds);

public interface IReflexHandler
{
    ReflexDefinition Definition { get; }
    Task<ReflexResult> EvaluateAsync(ReflexContext context, CancellationToken cancellationToken);
}
