using Relay.Core.Config;
using Relay.Core.Ids;
using Relay.Core.Input;
using Relay.Core.Storage;
using Relay.Tests.Support;

namespace Relay.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("F13", KeyModifiers.None, 0x7C, "F13")]
    [InlineData("f14", KeyModifiers.None, 0x7D, "F14")]
    [InlineData("Ctrl+Win+F24", KeyModifiers.Control | KeyModifiers.Win, 0x87, "Ctrl+Win+F24")]
    [InlineData("ctrl + alt + n", KeyModifiers.Control | KeyModifiers.Alt, 0x4E, "Ctrl+Alt+N")]
    [InlineData("Shift+Space", KeyModifiers.Shift, 0x20, "Shift+Space")]
    public void ParsesAndCanonicalizes(string input, KeyModifiers mods, int vk, string canonical)
    {
        var chord = KeyChord.Parse(input);
        Assert.Equal(mods, chord.Modifiers);
        Assert.Equal(vk, chord.VirtualKey);
        Assert.Equal(canonical, chord.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("F13+F14")]
    [InlineData("Hyper+X")]
    public void RejectsMalformedChords(string input)
    {
        Assert.False(KeyChord.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ModifierOnlyChordsAreAllowedOnlyWhenAskedFor()
    {
        Assert.True(KeyChord.TryParse("ctrl + alt", allowModifierOnly: true, out var chord, out _));
        Assert.True(chord.IsModifierOnly);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, chord.Modifiers);
        Assert.Equal(0, chord.VirtualKey);
        Assert.Equal("Ctrl+Alt", chord.ToString());

        Assert.False(KeyChord.TryParse("Ctrl+Alt", allowModifierOnly: false, out _, out var error));
        Assert.Contains("no non-modifier key", error);
        Assert.False(KeyChord.TryParse("+", allowModifierOnly: true, out _, out _));
    }
}

public class SettingsTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void CreatesDefaultsOnFirstRun()
    {
        _tmp.Root.EnsureLayout(new FixedClock(Harness.T0));
        var load = SettingsStore.Load(_tmp.Root);
        Assert.True(load.CreatedDefault);
        Assert.Empty(load.Problems);
        // Window-scoped by default: the chords work only while Relay is the active window, so Ctrl+X still cuts elsewhere.
        Assert.Equal(HotkeySettings.WindowScope, load.Settings.Hotkeys.Scope);
        Assert.Equal("Ctrl+Alt", load.Settings.Hotkeys.NoteKey);
        Assert.Equal("Ctrl+X", load.Settings.Hotkeys.CommandKey);
        Assert.False(load.Settings.FlowRelay.Enabled);
        Assert.True(File.Exists(_tmp.Root.SettingsPath));
    }

    [Fact]
    public void InvalidHotkeysFallBackToDefaultsAndAreReported()
    {
        _tmp.Root.EnsureLayout(new FixedClock(Harness.T0));
        AtomicFile.WriteAllText(_tmp.Root.SettingsPath, """{"hotkeys":{"noteKey":"Bogus","commandKey":"F14"}}""");
        var load = SettingsStore.Load(_tmp.Root);
        Assert.False(load.CreatedDefault);
        Assert.Contains(load.Problems, p => p.Contains("noteKey"));
        Assert.Equal("Ctrl+Alt", load.Settings.Hotkeys.NoteKey);
    }

    [Fact]
    public void IdenticalHotkeysAreRejected()
    {
        var s = new RelaySettings();
        s.Hotkeys.CommandKey = "Ctrl+Alt";
        Assert.Contains(s.Validate(), p => p.Contains("must differ"));
    }

    [Fact]
    public void GlobalScopeRequiresARealKeyAndScopeMustBeKnown()
    {
        var s = new RelaySettings();
        Assert.Empty(s.Validate());

        s.Hotkeys.Scope = HotkeySettings.GlobalScope;
        var problems = s.Validate();
        Assert.Contains(problems, p => p.StartsWith("hotkeys.noteKey") && p.Contains("global hotkey needs a key"));
        Assert.DoesNotContain(problems, p => p.StartsWith("hotkeys.commandKey"));

        s.Hotkeys.NoteKey = "F13";
        s.Hotkeys.CommandKey = "F14";
        Assert.Empty(s.Validate());

        s.Hotkeys.Scope = "everywhere";
        Assert.Contains(s.Validate(), p => p.Contains("hotkeys.scope"));
    }

    [Fact]
    public void RelayChordMustDifferFromPrimaryKeys()
    {
        var s = new RelaySettings();
        s.FlowRelay.Enabled = true;
        s.FlowRelay.HandsFreeChord = "Ctrl+X";
        Assert.Contains(s.Validate(), p => p.Contains("flowRelay.handsFreeChord"));
    }

    public void Dispose() => _tmp.Dispose();
}

public class UlidTests
{
    [Fact]
    public void GeneratesValidSortableIds()
    {
        var a = Ulid.NewUlid(Harness.T0);
        var b = Ulid.NewUlid(Harness.T0.AddMilliseconds(1));
        Assert.True(Ulid.IsValid(a));
        Assert.True(Ulid.IsValid(b));
        Assert.Equal(26, a.Length);
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.NotEqual(Ulid.NewUlid(Harness.T0), Ulid.NewUlid(Harness.T0));
    }
}

public class DataRootTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void LayoutIsCreatedOnceWithAManifest()
    {
        var clock = new FixedClock(Harness.T0);
        Assert.True(_tmp.Root.EnsureLayout(clock));
        Assert.False(_tmp.Root.EnsureLayout(clock));
        var manifest = _tmp.Root.ReadManifest()!;
        Assert.Equal(DataRoot.SchemaVersion, manifest.SchemaVersion);
        Assert.True(Ulid.IsValid(manifest.InstanceId));
        foreach (var dir in new[] { _tmp.Root.LedgerDirectory, _tmp.Root.DraftsDirectory, _tmp.Root.DraftNotesDirectory, _tmp.Root.IncidentsDirectory, _tmp.Root.SessionsDirectory })
        {
            Assert.True(Directory.Exists(dir), dir);
        }
    }

    public void Dispose() => _tmp.Dispose();
}
