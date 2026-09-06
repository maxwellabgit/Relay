namespace Relay.Core.Session;

/// <summary>
/// What the coordinator needs from the desktop shell. Kept deliberately small so the whole
/// capture lifecycle can be exercised in tests with a fake host. Relay never synthesizes input:
/// Wispr Flow is started and stopped with its own shortcut, and its text arrives on the capture
/// surface like any other typing.
/// </summary>
public interface ICaptureHost
{
    /// <summary>Clears the isolated capture surface and gives it keyboard focus in the foreground window.</summary>
    void PrepareCaptureSurface();
    /// <summary>Process name of the current foreground window, or null. Never returns window titles.</summary>
    string? ForegroundProcessName();
}
