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

public sealed record ReflexOutcomeHistory(
    int RunCount,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? LastRunAt);

/// <summary>
/// Versioned declarative automation. Policy lives here, not inside handler code.
/// </summary>
public sealed record ReflexDefinition(
    string Id,
    int Version,
    string DisplayName,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> NegativeTriggers,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> PermittedSources,
    IReadOnlyList<string> ReadPlan,
    IReadOnlyList<string> JudgmentDefinitionIds,
    IReadOnlyList<string> PermittedWriteActionIds,
    ApprovalMode ApprovalMode,
    ReflexBudgets Budgets,
    ReflexRetryPolicy RetryPolicy,
    IReadOnlyList<string> EvaluationFixtureIds,
    string ExplanationTemplate,
    ReflexActivationState DefaultActivation,
    bool SupportsRollback,
    ReflexOutcomeHistory? OutcomeHistory = null);

public sealed record ReflexContext(
    string CaseId,
    int CaseVersion,
    string ReflexId,
    int ReflexVersion,
    IReadOnlyList<SourceSliceRef> TriggerSourceRefs,
    IReadOnlyList<Connection> EligibleConnections,
    ReflexBudgets RemainingBudgets,
    DateTimeOffset Now);

public enum ReflexResultKind
{
    Finding,
    Evidence,
    Read,
    ProposeOperation,
    Clarification,
    NoAction,
}

public sealed record ReflexEvidenceDraft(
    string Kind,
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs);

public sealed record OperationProposal(
    string ConnectionId,
    string ConnectorId,
    int ConnectorVersion,
    string ActionId,
    int ActionVersion,
    string IdempotencyKey,
    string CanonicalHash,
    System.Text.Json.JsonElement Arguments,
    IReadOnlyList<SourceSliceRef> InputRefs,
    System.Text.Json.JsonElement GrantedScope,
    IReadOnlyList<OperationPrecondition> Preconditions);

public sealed record ReflexResult(
    ReflexResultKind Kind,
    string Summary,
    IReadOnlyList<SourceSliceRef> SourceRefs,
    IReadOnlyList<string> JudgmentIds,
    IReadOnlyList<ReflexEvidenceDraft> EvidenceDrafts,
    IReadOnlyList<ReadRequest> ReadRequests,
    IReadOnlyList<OperationProposal> OperationProposals,
    string? ClarificationPrompt);

public interface IReflexHandler
{
    ReflexDefinition Definition { get; }
    Task<ReflexResult> EvaluateAsync(ReflexContext context, CancellationToken cancellationToken);
}
