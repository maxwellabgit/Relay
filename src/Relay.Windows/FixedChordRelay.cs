using System.Runtime.InteropServices;
using Relay.Core.Input;
using Relay.Core.Session;
using static Relay.Windows.NativeMethods;

namespace Relay.Windows;

/// <summary>
/// The Flow relay adapter (contract §5, §14). It can emit exactly one chord, fixed at
/// construction from user configuration, and nothing else: there is no member that accepts a
/// key, a string, or a sequence. The coordinator additionally refuses to call it unless Relay's
/// own capture surface is the foreground target, so the chord can never reach another app.
/// </summary>
public sealed class FixedChordRelay : IFlowRelay
{
    private readonly KeyChord _chord;

    public FixedChordRelay(KeyChord chord)
    {
        _chord = chord;
    }

    public bool Enabled => true;
    public KeyChord? Chord => _chord;

    public RelayResult SendHandsFreeToggle(RelayPurpose purpose)
    {
        var modifiers = new List<ushort>(4);
        if (_chord.Modifiers.HasFlag(KeyModifiers.Control)) modifiers.Add(VK_CONTROL);
        if (_chord.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers.Add(VK_MENU);
        if (_chord.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers.Add(VK_SHIFT);
        if (_chord.Modifiers.HasFlag(KeyModifiers.Win)) modifiers.Add(VK_LWIN);

        var inputs = new INPUT[(modifiers.Count + 1) * 2];
        var i = 0;
        foreach (var vk in modifiers) inputs[i++] = Key(vk, up: false);
        inputs[i++] = Key(_chord.VirtualKey, up: false);
        inputs[i++] = Key(_chord.VirtualKey, up: true);
        for (var m = modifiers.Count - 1; m >= 0; m--) inputs[i++] = Key(modifiers[m], up: true);

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return new RelayResult(true, null);
        var error = Marshal.GetLastWin32Error();
        return new RelayResult(false, $"SendInput delivered {sent} of {inputs.Length} events (Win32 error {error})");
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };
}
