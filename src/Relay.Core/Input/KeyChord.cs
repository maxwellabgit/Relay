namespace Relay.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,      // MOD_ALT
    Control = 2,  // MOD_CONTROL
    Shift = 4,    // MOD_SHIFT
    Win = 8,      // MOD_WIN
}

/// <summary>
/// A key combination expressed as modifiers plus one virtual-key code, parsed from a
/// human-readable string such as <c>F13</c>, <c>Ctrl+Alt+N</c> or <c>Ctrl+Win+F24</c>.
/// Virtual-key values follow the Win32 table so the Windows layer can pass them straight
/// to RegisterHotKey without a second mapping. A chord made of modifiers only (<c>Ctrl+Alt</c>)
/// is valid for window-local accelerators, which see raw key state, but not for global
/// registration, which needs a key.
/// </summary>
public sealed record KeyChord(KeyModifiers Modifiers, ushort VirtualKey, string KeyName)
{
    public bool IsModifierOnly => VirtualKey == 0;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        if (!IsModifierOnly) parts.Add(KeyName);
        return string.Join("+", parts);
    }

    public static KeyChord Parse(string text)
    {
        if (!TryParse(text, out var chord, out var error)) throw new FormatException(error);
        return chord;
    }

    public static bool TryParse(string? text, out KeyChord chord, out string error) => TryParse(text, allowModifierOnly: false, out chord, out error);

    public static bool TryParse(string? text, bool allowModifierOnly, out KeyChord chord, out string error)
    {
        chord = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Key chord is empty.";
            return false;
        }

        var modifiers = KeyModifiers.None;
        string? keyName = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": modifiers |= KeyModifiers.Control; continue;
                case "ALT": modifiers |= KeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= KeyModifiers.Shift; continue;
                case "WIN":
                case "WINDOWS":
                case "SUPER": modifiers |= KeyModifiers.Win; continue;
            }
            if (keyName is not null)
            {
                error = $"Key chord '{text}' names more than one non-modifier key.";
                return false;
            }
            keyName = raw;
        }

        if (keyName is null)
        {
            if (allowModifierOnly && modifiers != KeyModifiers.None)
            {
                chord = new KeyChord(modifiers, 0, "");
                return true;
            }
            error = allowModifierOnly ? $"Key chord '{text}' names no key." : $"Key chord '{text}' has no non-modifier key.";
            return false;
        }
        if (!VirtualKeys.TryLookup(keyName, out var vk, out var canonical))
        {
            error = $"Unknown key '{keyName}' in chord '{text}'.";
            return false;
        }
        chord = new KeyChord(modifiers, vk, canonical);
        return true;
    }
}

public static class VirtualKeys
{
    private static readonly Dictionary<string, (ushort Vk, string Canonical)> Table = Build();

    public static bool TryLookup(string name, out ushort vk, out string canonical)
    {
        if (Table.TryGetValue(name.ToUpperInvariant(), out var entry))
        {
            vk = entry.Vk;
            canonical = entry.Canonical;
            return true;
        }
        vk = 0;
        canonical = "";
        return false;
    }

    private static Dictionary<string, (ushort, string)> Build()
    {
        var t = new Dictionary<string, (ushort, string)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= 24; i++) t[$"F{i}"] = ((ushort)(0x6F + i), $"F{i}");
        for (var c = 'A'; c <= 'Z'; c++) t[c.ToString()] = ((ushort)c, c.ToString());
        for (var d = '0'; d <= '9'; d++) t[d.ToString()] = ((ushort)d, d.ToString());
        t["SPACE"] = (0x20, "Space");
        t["TAB"] = (0x09, "Tab");
        t["ENTER"] = (0x0D, "Enter");
        t["RETURN"] = (0x0D, "Enter");
        t["ESC"] = (0x1B, "Esc");
        t["ESCAPE"] = (0x1B, "Esc");
        t["PAUSE"] = (0x13, "Pause");
        t["INSERT"] = (0x2D, "Insert");
        t["HOME"] = (0x24, "Home");
        t["END"] = (0x23, "End");
        t["PAGEUP"] = (0x21, "PageUp");
        t["PAGEDOWN"] = (0x22, "PageDown");
        t["SCROLLLOCK"] = (0x91, "ScrollLock");
        t["PRINTSCREEN"] = (0x2C, "PrintScreen");
        t["NUMPAD0"] = (0x60, "Numpad0");
        t["NUMPAD1"] = (0x61, "Numpad1");
        t["NUMPAD2"] = (0x62, "Numpad2");
        t["NUMPAD3"] = (0x63, "Numpad3");
        t["NUMPAD4"] = (0x64, "Numpad4");
        t["NUMPAD5"] = (0x65, "Numpad5");
        t["NUMPAD6"] = (0x66, "Numpad6");
        t["NUMPAD7"] = (0x67, "Numpad7");
        t["NUMPAD8"] = (0x68, "Numpad8");
        t["NUMPAD9"] = (0x69, "Numpad9");
        return t;
    }
}
