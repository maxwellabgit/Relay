using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Cases;

/// <summary>One feed row as the UI surface reads it (from projections).</summary>
public sealed record FeedProjectionItem(
    [property: JsonPropertyName("feedId")] string FeedId,
    [property: JsonPropertyName("caseId")] string? CaseId,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] string? Level);

/// <summary>Pending approval card for the surface.</summary>
public sealed record PendingApprovalView(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("caseVersion")] long CaseVersion,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("envelopeHash")] string EnvelopeHash,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, JsonElement> Arguments);

/// <summary>Placeholder model health until a live local model is bound on Windows.</summary>
public sealed record ModelHealthView(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail = null)
{
    public const string Unknown = "unknown";
    public const string Unavailable = "unavailable";
    public const string Ok = "ok";

    public static ModelHealthView Placeholder => new(Unavailable, "No local model bound on this host; Windows live gate required.");
}

/// <summary>
/// One snapshot the Desktop layer may bind to: a single feed, pending approvals,
/// model/Jev health, listening flag, and hosted-grant status. Orchestration state stays in Core.
/// </summary>
public sealed record RelaySurfaceSnapshot(
    [property: JsonPropertyName("feed")] IReadOnlyList<FeedProjectionItem> Feed,
    [property: JsonPropertyName("pendingApprovals")] IReadOnlyList<PendingApprovalView> PendingApprovals,
    [property: JsonPropertyName("modelHealth")] ModelHealthView ModelHealth,
    [property: JsonPropertyName("listening")] bool Listening,
    [property: JsonPropertyName("listeningCaseId")] string? ListeningCaseId,
    [property: JsonPropertyName("composerCaseId")] string? ComposerCaseId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("hostedJudgments")] Privacy.HostedJudgmentView? HostedJudgments = null);

/// <summary>Result of a surface command.</summary>
public sealed record SurfaceResult(
    bool Ok,
    string Summary,
    string? CaseId = null,
    string? OperationId = null,
    string? Error = null,
    string? GrantId = null)
{
    public static SurfaceResult Success(string summary, string? caseId = null, string? operationId = null, string? grantId = null)
        => new(true, summary, caseId, operationId, GrantId: grantId);
    public static SurfaceResult Fail(string error)
        => new(false, "failed", Error: error);
}

/// <summary>
/// The only UI↔Core contract for Slice 6+: read one feed projection, submit composer and
/// approval commands, and manage hosted-judgment grants. Desktop must not own orchestration state.
/// </summary>
public interface IRelaySurface
{
    RelaySurfaceSnapshot Snapshot();

    SurfaceResult ToggleListening();
    SurfaceResult SubmitComposer(string text, string kind = CaseKind.Answer);
    SurfaceResult ApproveOperation(string operationId, string envelopeHash, long expectedCaseVersion);
    SurfaceResult RejectOperation(string operationId, string? reason = null);
    SurfaceResult EditOperation(string operationId, Dictionary<string, JsonElement> newArguments);
    SurfaceResult CancelCase(string caseId, string? reason = null);

    SurfaceResult GrantHostedSession(
        string sessionId,
        IReadOnlyList<string> purposes,
        int maximumInputTokenBudget,
        DateTimeOffset? expiresAt = null);
    SurfaceResult GrantHostedProject(
        string projectId,
        IReadOnlyList<string> purposes,
        int maximumInputTokenBudget,
        DateTimeOffset? expiresAt = null);
    SurfaceResult RevokeHostedGrant(string grantId);

    /// <summary>Advance work after a command (tests / harness). UI may poll Snapshot instead.</summary>
    Task<SurfaceResult> RunUntilIdleAsync(string? caseId = null, int maxSteps = 16, CancellationToken cancellationToken = default);
}

/// <summary>Command names the surface accepts (for logging / harness).</summary>
public static class RelaySurfaceCommands
{
    public const string ToggleListening = "ToggleListening";
    public const string SubmitComposer = "SubmitComposer";
    public const string ApproveOperation = "ApproveOperation";
    public const string RejectOperation = "RejectOperation";
    public const string EditOperation = "EditOperation";
    public const string CancelCase = "CancelCase";
    public const string GrantHostedSession = "GrantHostedSession";
    public const string GrantHostedProject = "GrantHostedProject";
    public const string RevokeHostedGrant = "RevokeHostedGrant";
}
