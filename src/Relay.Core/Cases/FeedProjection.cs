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

public sealed record ComponentHealthView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail = null);

/// <summary>Separate health for Jev / local / search / reasoning.</summary>
public sealed record ServiceHealthView(
    [property: JsonPropertyName("jev")] ComponentHealthView Jev,
    [property: JsonPropertyName("localModel")] ComponentHealthView LocalModel,
    [property: JsonPropertyName("search")] ComponentHealthView Search,
    [property: JsonPropertyName("reasoning")] ComponentHealthView Reasoning)
{
    public static ServiceHealthView UnavailablePlaceholder => new(
        new ComponentHealthView("jev", ModelHealthView.Unavailable, "No Jev client bound"),
        new ComponentHealthView("local", ModelHealthView.Unavailable, "No local model bound"),
        new ComponentHealthView("search", ModelHealthView.Unavailable, "No search client bound"),
        new ComponentHealthView("reasoning", ModelHealthView.Unavailable, "No reasoning client bound"));
}

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

public sealed record CaptureStatusView(
    [property: JsonPropertyName("captureEnabled")] bool CaptureEnabled,
    [property: JsonPropertyName("pendingSegmentCount")] int PendingSegmentCount,
    [property: JsonPropertyName("pendingWindowCount")] int PendingWindowCount,
    [property: JsonPropertyName("hostedGrantId")] string? HostedGrantId);

public sealed record CaseStatusView(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("waitingReason")] string? WaitingReason,
    [property: JsonPropertyName("origin")] string Origin);

public sealed record SourceConflictView(
    [property: JsonPropertyName("conflictId")] string ConflictId,
    [property: JsonPropertyName("statementIds")] IReadOnlyList<string> StatementIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record RetentionControlView(
    [property: JsonPropertyName("defaultTtlDays")] int DefaultTtlDays,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("sessionTtlDays")] double? SessionTtlDays);

public sealed record CapabilityView(
    [property: JsonPropertyName("bundleId")] string BundleId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("active")] bool Active);

/// <summary>
/// One snapshot the Desktop layer may bind to. Orchestration state stays in Core.
/// </summary>
public sealed record RelaySurfaceSnapshot(
    [property: JsonPropertyName("feed")] IReadOnlyList<FeedProjectionItem> Feed,
    [property: JsonPropertyName("pendingApprovals")] IReadOnlyList<PendingApprovalView> PendingApprovals,
    [property: JsonPropertyName("modelHealth")] ModelHealthView ModelHealth,
    [property: JsonPropertyName("listening")] bool Listening,
    [property: JsonPropertyName("listeningCaseId")] string? ListeningCaseId,
    [property: JsonPropertyName("composerCaseId")] string? ComposerCaseId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("hostedProcessingEnabled")] bool HostedProcessingEnabled = false,
    [property: JsonPropertyName("hostedGrantId")] string? HostedGrantId = null,
    [property: JsonPropertyName("capture")] CaptureStatusView? Capture = null,
    [property: JsonPropertyName("cases")] IReadOnlyList<CaseStatusView>? Cases = null,
    [property: JsonPropertyName("serviceHealth")] ServiceHealthView? ServiceHealth = null,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<SourceConflictView>? Conflicts = null,
    [property: JsonPropertyName("retention")] RetentionControlView? Retention = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<CapabilityView>? Capabilities = null);

/// <summary>Result of a surface command.</summary>
public sealed record SurfaceResult(
    bool Ok,
    string Summary,
    string? CaseId = null,
    string? OperationId = null,
    string? Error = null)
{
    public static SurfaceResult Success(string summary, string? caseId = null, string? operationId = null)
        => new(true, summary, caseId, operationId);
    public static SurfaceResult Fail(string error)
        => new(false, "failed", Error: error);
}

/// <summary>
/// UI↔Core contract: feed, composer, approvals, listening/hosted controls, retention, capabilities.
/// Desktop must not own orchestration state.
/// </summary>
public interface IRelaySurface
{
    RelaySurfaceSnapshot Snapshot();

    SurfaceResult ToggleListening();
    SurfaceResult SetHostedProcessing(bool enabled, string? grantId = null);
    SurfaceResult SubmitComposer(string text, string kind = CaseKind.Answer);
    SurfaceResult ApproveOperation(string operationId, string envelopeHash, long expectedCaseVersion);
    SurfaceResult RejectOperation(string operationId, string? reason = null);
    SurfaceResult EditOperation(string operationId, Dictionary<string, JsonElement> newArguments);
    SurfaceResult CancelCase(string caseId, string? reason = null);
    SurfaceResult SetRetentionPolicy(string sessionId, double ttlDays, bool deleteExcerptsWithSession = false);
    SurfaceResult DeleteSessionNow(string sessionId, bool deleteExcerpts = false);

    /// <summary>Advance work after a command (tests / harness). UI may poll Snapshot instead.</summary>
    Task<SurfaceResult> RunUntilIdleAsync(string? caseId = null, int maxSteps = 16, CancellationToken cancellationToken = default);
}

/// <summary>Command names the surface accepts (for logging / harness).</summary>
public static class RelaySurfaceCommands
{
    public const string ToggleListening = "ToggleListening";
    public const string SetHostedProcessing = "SetHostedProcessing";
    public const string SubmitComposer = "SubmitComposer";
    public const string ApproveOperation = "ApproveOperation";
    public const string RejectOperation = "RejectOperation";
    public const string EditOperation = "EditOperation";
    public const string CancelCase = "CancelCase";
    public const string SetRetentionPolicy = "SetRetentionPolicy";
    public const string DeleteSessionNow = "DeleteSessionNow";
}
