using Relay.Core.Input;

namespace Relay.Core.Session;

/// <summary>
/// What the coordinator needs from the desktop shell. Kept deliberately small so the whole
/// capture lifecycle can be exercised in tests with a fake host.
/// </summary>
public interface ICaptureHost
{
    /// <summary>Clears the isolated capture surface and gives it keyboard focus in the foreground window.</summary>
    void PrepareCaptureSurface();
    /// <summary>True when Relay's window is the foreground window and the capture surface has focus.</summary>
    bool IsCaptureSurfaceForeground();
    /// <summary>Process name of the current foreground window, or null. Never returns window titles.</summary>
    string? ForegroundProcessName();
}

public enum RelayPurpose
{
    Start,
    Stop,
}

public sealed record RelayResult(bool Sent, string? Error);

/// <summary>
/// The Flow relay adapter. An implementation may emit exactly one fixed, user-configured chord.
/// There is intentionally no API that accepts an arbitrary key.
/// </summary>
public interface IFlowRelay
{
    bool Enabled { get; }
    KeyChord? Chord { get; }
    RelayResult SendHandsFreeToggle(RelayPurpose purpose);
}

public sealed class DisabledFlowRelay : IFlowRelay
{
    public static readonly DisabledFlowRelay Instance = new();
    public bool Enabled => false;
    public KeyChord? Chord => null;
    public RelayResult SendHandsFreeToggle(RelayPurpose purpose) => new(false, "relay disabled");
}
