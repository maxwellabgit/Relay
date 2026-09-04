using Relay.Core.Ledger;
using Relay.Core.State;

namespace Relay.Core.Session;

public enum ReviewItemKind
{
    /// <summary>A capture found at startup that never committed. Actions: Commit, Discard.</summary>
    InterruptedCapture,
    /// <summary>The text of a capture the user cancelled during this session. Actions: Recover draft, Forget.</summary>
    CancelledDraft,
    /// <summary>The most recent command-mode instruction. Informational: no tools run in this build.</summary>
    RecordedInstruction,
    /// <summary>Settings that were rejected and replaced by defaults.</summary>
    SettingsProblem,
    /// <summary>A hotkey that could not be registered.</summary>
    HotkeyProblem,
    /// <summary>Why the system is LOCKED or FAILED. Actions: Unlock / Retry / Return to Idle.</summary>
    Incident,
}

public sealed record ReviewItem(ReviewItemKind Kind, string Title, string Detail, string? Payload = null);

public sealed record ActivityEntry(long Seq, DateTimeOffset Timestamp, string Type, string Text);

public sealed record HotkeyStatus(string Name, string Chord, bool Registered, string? Error);

public sealed record AwaitingStatus(bool TimedOut, int Extensions, bool StabilizationPending);

public sealed record IncidentInfo(string Kind, string Summary, string Detail, DateTimeOffset At, string? IncidentFilePath);

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
    bool CanRetry)
{
    public bool CanCancel => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;
    public bool CanSubmitNow => State == RelayState.AwaitingTranscript && CaptureChars > 0;
    public bool CanRetryWait => State == RelayState.AwaitingTranscript && Awaiting?.TimedOut == true;
    public bool SurfaceEditable => State is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;
}
