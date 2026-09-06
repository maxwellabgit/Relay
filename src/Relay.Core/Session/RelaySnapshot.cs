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
    /// <summary>An instruction was mid-turn when the previous process ended. Informational.</summary>
    TurnInterrupted,
    /// <summary>A controlled write started but never finished in a previous run. Payload = proposalId.</summary>
    ExecutionInterrupted,
    /// <summary>Two notes that appear to conflict. Actions: keep both / supersede. Payload = json.</summary>
    DisputedNotes,
    /// <summary>Content that could not be indexed or read (e.g. a hand-edited note file).</summary>
    IndexProblem,
}

public sealed record ReviewItem(ReviewItemKind Kind, string Title, string Detail, string? Payload = null);

public sealed record ActivityEntry(long Seq, DateTimeOffset Timestamp, string Type, string Text);

/// <summary><paramref name="Scope"/> is "window" (active only while Relay is the foreground window) or "global".</summary>
public sealed record HotkeyStatus(string Name, string Chord, bool Registered, string? Error, string Scope = "global")
{
    public bool WindowScoped => Scope == "window";
}

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

/// <summary>A registered project folder. Registration happens as a side effect of creating a project in a new folder; it is never proposed.</summary>
public sealed record WorkspaceView(string Path, string? Label, bool Present);

/// <summary>
/// One unrouted note waiting in staging. When routing was uncertain, <paramref name="Candidates"/>
/// holds the projects the router considered (best first) and <paramref name="Summary"/> says why it
/// did not decide; otherwise both are empty and the note simply had no home. Actions: file under a
/// project, or keep it here (which retires the suggestions).
/// </summary>
public sealed record InboxItem(string NoteId, string Type, DateTimeOffset CreatedAt, string Text, string? Summary, IReadOnlyList<Memory.RoutingCandidate> Candidates)
{
    public bool HasSuggestions => Candidates.Count > 0;
}

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
    IReadOnlyList<InboxItem> Inbox,
    string OrchestratorMode,
    string OrchestratorName,
    bool ModelEnabled,
    string? ModelEndpoint,
    string? ModelName,
    bool ModelKeyStored = false)
{
    public bool CanCancel => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript or RelayState.Planning or RelayState.AwaitingApproval or RelayState.Executing;
    public bool CanSubmitNow => State == RelayState.AwaitingTranscript && CaptureChars > 0;
    public bool CanRetryWait => State == RelayState.AwaitingTranscript && Awaiting?.TimedOut == true;
    public bool SurfaceEditable => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;
    public bool TurnActive => State.IsTurnActive();
    public IEnumerable<ProposalView> PendingProposals => Response?.Proposals.Where(p => p.Status == "pending") ?? [];
}
