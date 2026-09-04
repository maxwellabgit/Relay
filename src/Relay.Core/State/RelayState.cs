namespace Relay.Core.State;

/// <summary>
/// The single primary state the UI must always display (contract §4). Planning,
/// AwaitingApproval and Executing are defined so the table is complete, but nothing in the
/// v0.1 slice can enter them.
/// </summary>
public enum RelayState
{
    Starting,
    Idle,
    NoteCapture,
    CommandCapture,
    AwaitingTranscript,
    Organizing,
    Planning,
    AwaitingApproval,
    Executing,
    Completed,
    Failed,
    Locked,
}

public enum CaptureMode
{
    Note,
    Command,
}

public enum Trigger
{
    RecoveryCompleted,
    RecoveryLocked,
    NoteKey,
    CommandKey,
    Cancel,
    TranscriptStable,
    TranscriptTimeout,
    RetryWait,
    SubmitNow,
    OrganizeSucceeded,
    OrganizeFailed,
    /// <summary>User chose to store an interrupted capture found at startup (IDLE → ORGANIZING).</summary>
    CommitInterrupted,
    /// <summary>User chose to store the text of a capture cancelled earlier this session (IDLE → ORGANIZING).</summary>
    RecoverDraft,
    Dismiss,
    Retry,
    /// <summary>An unexpected failure was reported; work stops and the user inspects it (→ FAILED).</summary>
    Fail,
    /// <summary>Integrity or policy protection stops the system (→ LOCKED).</summary>
    Lock,
    Unlock,

    // Orchestrator turns (phases 3–4)
    /// <summary>A command capture was stored and the orchestrator is enabled (ORGANIZING → PLANNING).</summary>
    BeginPlanning,
    /// <summary>The plan has no proposals that need approval or execution (PLANNING → COMPLETED).</summary>
    PlanReady,
    /// <summary>At least one proposal needs the user's decision (PLANNING → AWAITING_APPROVAL).</summary>
    ApprovalRequired,
    /// <summary>Approved or automatically allowed operations are about to run (→ EXECUTING). Also used for user-initiated operations from IDLE.</summary>
    BeginExecution,
    /// <summary>Every pending proposal was rejected; nothing runs (AWAITING_APPROVAL → COMPLETED).</summary>
    AllRejected,
    PlanFailed,
    ExecutionSucceeded,
    ExecutionFailed,
}

public static class RelayStateExtensions
{
    /// <summary>Display label exactly as the contract names the state.</summary>
    public static string Label(this RelayState state) => state switch
    {
        RelayState.Starting => "STARTING",
        RelayState.Idle => "IDLE",
        RelayState.NoteCapture => "NOTE_CAPTURE",
        RelayState.CommandCapture => "COMMAND_CAPTURE",
        RelayState.AwaitingTranscript => "AWAITING_TRANSCRIPT",
        RelayState.Organizing => "ORGANIZING",
        RelayState.Planning => "PLANNING",
        RelayState.AwaitingApproval => "AWAITING_APPROVAL",
        RelayState.Executing => "EXECUTING",
        RelayState.Completed => "COMPLETED",
        RelayState.Failed => "FAILED",
        RelayState.Locked => "LOCKED",
        _ => state.ToString().ToUpperInvariant(),
    };

    public static bool IsCapturing(this RelayState state)
        => state is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;

    /// <summary>States in which an orchestrator turn owns the session.</summary>
    public static bool IsTurnActive(this RelayState state)
        => state is RelayState.Planning or RelayState.AwaitingApproval or RelayState.Executing;

    public static string Label(this CaptureMode mode) => mode switch
    {
        CaptureMode.Note => "Note",
        CaptureMode.Command => "Command",
        _ => mode.ToString(),
    };

    public static string WireName(this CaptureMode mode) => mode switch
    {
        CaptureMode.Note => "note",
        CaptureMode.Command => "command",
        _ => mode.ToString().ToLowerInvariant(),
    };

    public static CaptureMode ParseMode(string? wire) => wire switch
    {
        "note" => CaptureMode.Note,
        "command" => CaptureMode.Command,
        _ => throw new FormatException($"Unknown capture mode '{wire}'."),
    };
}
