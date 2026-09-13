namespace Relay.Core.State;

/// <summary>
/// The session machine: whether Relay is up, ready for work, failed, or locked. Capture, listening,
/// and turns live elsewhere — on surface flags and on each task — so many tasks and a held chord can
/// coexist without locking the session into a turn state.
/// </summary>
public enum RelayState
{
    Starting,
    Ready,
    Failed,
    Locked,
}

/// <summary>
/// Where the capture surface is in its own short life. Independent of <see cref="RelayState"/>: the
/// session stays Ready the whole time, and the UI reads this flag (plus <see cref="CaptureMode"/>)
/// for the box, the meters and the cancel/submit buttons.
/// </summary>
public enum CapturePhase
{
    None,
    Capturing,
    AwaitingTranscript,
    Organizing,
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
    Cancel,
    Dismiss,
    Retry,
    /// <summary>An unexpected failure was reported; work stops and the user inspects it (→ FAILED).</summary>
    Fail,
    /// <summary>Integrity or policy protection stops the system (→ LOCKED).</summary>
    Lock,
    Unlock,
}

public static class RelayStateExtensions
{
    /// <summary>Display label exactly as the contract names the state.</summary>
    public static string Label(this RelayState state) => state switch
    {
        RelayState.Starting => "STARTING",
        RelayState.Ready => "READY",
        RelayState.Failed => "FAILED",
        RelayState.Locked => "LOCKED",
        _ => state.ToString().ToUpperInvariant(),
    };

    public static string Label(this CapturePhase phase) => phase switch
    {
        CapturePhase.None => "NONE",
        CapturePhase.Capturing => "CAPTURING",
        CapturePhase.AwaitingTranscript => "AWAITING_TRANSCRIPT",
        CapturePhase.Organizing => "ORGANIZING",
        _ => phase.ToString().ToUpperInvariant(),
    };

    public static bool IsCapturing(this CapturePhase phase)
        => phase is CapturePhase.Capturing or CapturePhase.AwaitingTranscript;

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
