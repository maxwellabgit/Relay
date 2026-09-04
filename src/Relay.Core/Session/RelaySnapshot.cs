using Relay.Core.Ledger;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.State;

namespace Relay.Core.Session;

public enum ReviewItemKind
{
    /// <summary>A capture found at startup that never committed. Actions: Commit, Discard.</summary>
    InterruptedCapture,
    /// <summary>The text of a capture the user cancelled during this session. Actions: Recover draft, Forget.</summary>
    CancelledDraft,
    /// <summary>The most recent command-mode instruction when the orchestrator is off. Informational.</summary>
    RecordedInstruction,
    /// <summary>Settings that were rejected and replaced by defaults.</summary>
    SettingsProblem,
    /// <summary>A hotkey that could not be registered.</summary>
    HotkeyProblem,
    /// <summary>Why the system is LOCKED or FAILED. Actions: Unlock / Retry / Return to Idle.</summary>
    Incident,
    /// <summary>A proposal awaiting the user's decision. Actions: Approve, Edit, Reject. Payload = proposalId.</summary>
    Proposal,
    /// <summary>An instruction was mid-turn when the previous process ended. Informational.</summary>
    TurnInterrupted,
    /// <summary>A controlled write started but never finished in a previous run. Payload = proposalId.</summary>
    ExecutionInterrupted,
    /// <summary>A note whose project routing is uncertain. Actions: choose project / keep unrouted. Payload = noteId.</summary>
    RoutingDecision,
    /// <summary>Two notes that appear to conflict. Actions: keep both / supersede. Payload = json.</summary>
    DisputedNotes,
    /// <summary>Content that could not be indexed or read (e.g. a hand-edited note file).</summary>
    IndexProblem,
}

public sealed record ReviewItem(ReviewItemKind Kind, string Title, string Detail, string? Payload = null);

public sealed record ActivityEntry(long Seq, DateTimeOffset Timestamp, string Type, string Text);

public sealed record HotkeyStatus(string Name, string Chord, bool Registered, string? Error);

public sealed record AwaitingStatus(bool TimedOut, int Extensions, bool StabilizationPending);

public sealed record IncidentInfo(string Kind, string Summary, string Detail, DateTimeOffset At, string? IncidentFilePath);

public sealed record ProposalView(
    string ProposalId,
    string Action,
    string Title,
    string Detail,
    IReadOnlyDictionary<string, string> Target,
    Tier Tier,
    string Status,
    IReadOnlyList<string> Reasons,
    string? ResultSummary,
    string? Error,
    bool Editable,
    string ProposedBy,
    string Reason);

/// <summary>The orchestrator's visible output for the current or most recent turn.</summary>
public sealed record TurnResponse(
    string TurnId,
    string Kind,
    string Instruction,
    string Summary,
    IReadOnlyList<string> Steps,
    string? Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<ProposalView> Proposals,
    string Producer,
    string? Outcome,
    DateTimeOffset StartedAt,
    bool Live);

public sealed record ProjectView(string Id, string Slug, string Name, string Status, string RootPath, bool FolderPresent);

public sealed record WorkspaceView(string Path, string? Label, bool Present);

public sealed record DraftNoteView(string NoteId, string Type, DateTimeOffset CreatedAt, string Text);

/// <summary>Everything the UI renders. Rebuilt by the coordinator after every change.</summary>
public sealed record RelaySnapshot(
    RelayState State,
    CaptureMode? Mode,
    string? CaptureId,
    DateTimeOffset? CaptureStartedAt,
    int CaptureChars,
    bool CaptureSurfaceFocused,
    AwaitingStatus? Awaiting,
    string? Notice,
    string? Receipt,
    IncidentInfo? Incident,
    IReadOnlyList<ReviewItem> Review,
    IReadOnlyList<ActivityEntry> Activity,
    HotkeyStatus NoteKey,
    HotkeyStatus CommandKey,
    bool FlowRelayEnabled,
    string? FlowRelayChord,
    string LedgerPath,
    long LedgerRecords,
    string LedgerLastHash,
    LedgerHealth LedgerHealth,
    string SessionId,
    string DataRootPath,
    string AppVersion,
    int ProcessId,
    bool CanRetry,
    TurnResponse? Response,
    IReadOnlyList<ProjectView> Projects,
    IReadOnlyList<WorkspaceView> Workspaces,
    IReadOnlyList<DraftNoteView> DraftNotes,
    string OrchestratorMode,
    string OrchestratorName,
    bool ModelEnabled,
    string? ModelEndpoint,
    string? ModelName)
{
    public bool CanCancel => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript or RelayState.Planning or RelayState.AwaitingApproval or RelayState.Executing;
    public bool CanSubmitNow => State == RelayState.AwaitingTranscript && CaptureChars > 0;
    public bool CanRetryWait => State == RelayState.AwaitingTranscript && Awaiting?.TimedOut == true;
    public bool SurfaceEditable => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;
    public bool TurnActive => State.IsTurnActive();
    public IEnumerable<ProposalView> PendingProposals => Response?.Proposals.Where(p => p.Status == "pending") ?? [];
}
