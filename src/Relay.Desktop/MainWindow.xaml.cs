using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Relay.Core.Config;
using Relay.Core.Input;
using Relay.Core.Ledger;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using Relay.Windows;
using Windows.UI;
using TaskStatus = Relay.Core.Tasks.TaskStatus;
using VirtualKey = Windows.System.VirtualKey;

namespace Relay.Desktop;

public sealed class ActivityRow
{
    public required string Time { get; init; }
    public required string Text { get; init; }
    public required string Type { get; init; }
}

/// <summary>
/// The single window. It renders the coordinator's snapshot and forwards user decisions; it holds
/// no state of its own beyond render caches and which task the diagnostics drawer is open on.
/// Regions: status (with process tags), capture or listening (with the ask box), response,
/// attention, review, inbox, tasks, projects, relay (preferences and change sets), activity, diagnostics.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly ObservableCollection<ActivityRow> _activity = new();
    private readonly DispatcherQueueTimer _tick;
    private SessionCoordinator? _coordinator;
    private RelayRuntime? _runtime;
    private bool _suppressTextChanged;
    private long _lastActivitySeq;
    private string _reviewSignature = "";
    private string _responseSignature = "";
    private string _inboxSignature = "";
    private string _projectsSignature = "";
    private string _attentionSignature = "";
    private string _tasksSignature = "";
    private string _relaySignature = "";
    private string _processSignature = "";
    private string _taskDiagnosticsSignature = "";
    private string? _diagnosticsTaskId;
    private RelaySnapshot? _snapshot;
    private KeyChord? _noteChord;
    private KeyChord? _commandChord;
    private Action? _noteAction;
    private Action? _commandAction;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CaptureHost = new Host(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        AppWindow.Title = "Relay";
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 224);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 120, 120, 128);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(24, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(40, 255, 255, 255);

        var scale = WindowMetrics.ScaleFor(_hwnd);
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(680 * scale), (int)(980 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
        }

        ActivityList.ItemsSource = _activity;
        Activated += OnActivated;

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(500);
        _tick.Tick += (_, _) =>
        {
            if (_snapshot is null) return;
            RenderStatus(_snapshot);
            RenderListening(_snapshot);
            if (DiagnosticsExpander.IsExpanded) RenderDiagnostics();
        };
    }

    public ICaptureHost CaptureHost { get; }

    public void Attach(SessionCoordinator coordinator, RelayRuntime runtime)
    {
        _coordinator = coordinator;
        _runtime = runtime;
        coordinator.Changed += Render;
        Render();
    }

    public void ShowStartupFailure(DataRoot root, Exception ex)
    {
        MainScroll.Visibility = Visibility.Collapsed;
        StartupFailurePanel.Visibility = Visibility.Visible;
        StartupFailureText.Text = ex.ToString();
        StartupFailureHint.Text = $"Data root: {root.Path}\nIf another Relay is running against this folder, close it first. Details were written to {root.IncidentsDirectory} when possible.";
    }

    public void BringForward() => ForegroundWindows.BringToForeground(_hwnd);

    /// <summary>
    /// Window-scoped chords. They are matched against raw key state in this window's tunnelling key
    /// handler, so they only fire while Relay is the active window and nothing is registered with the
    /// system; Ctrl+X keeps meaning "cut" everywhere else. A modifier-only chord (Ctrl+Alt) fires when
    /// its last modifier goes down with exactly the others held.
    /// </summary>
    public void SetWindowChords(KeyChord? note, KeyChord? command, Action onNote, Action onCommand)
    {
        _noteChord = note;
        _commandChord = command;
        _noteAction = onNote;
        _commandAction = onCommand;
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return; // auto-repeat while held
        if (_noteChord is { } note && WindowChords.Matches(note, e.Key))
        {
            e.Handled = true;
            _noteAction?.Invoke();
        }
        else if (_commandChord is { } command && WindowChords.Matches(command, e.Key))
        {
            e.Handled = true;
            _commandAction?.Invoke();
        }
    }

    private static class WindowChords
    {
        public static bool Matches(KeyChord chord, VirtualKey pressed)
        {
            var pressedModifier = ModifierOf(pressed);
            var held = Held() | pressedModifier;
            if (chord.IsModifierOnly) return pressedModifier != KeyModifiers.None && held == chord.Modifiers;
            return pressedModifier == KeyModifiers.None && (ushort)pressed == chord.VirtualKey && held == chord.Modifiers;
        }

        private static KeyModifiers Held()
        {
            var held = KeyModifiers.None;
            if (Down(VirtualKey.Control)) held |= KeyModifiers.Control;
            if (Down(VirtualKey.Menu)) held |= KeyModifiers.Alt;
            if (Down(VirtualKey.Shift)) held |= KeyModifiers.Shift;
            if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) held |= KeyModifiers.Win;
            return held;
        }

        private static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        private static KeyModifiers ModifierOf(VirtualKey key) => key switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => KeyModifiers.Control,
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => KeyModifiers.Alt,
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => KeyModifiers.Shift,
            VirtualKey.LeftWindows or VirtualKey.RightWindows => KeyModifiers.Win,
            _ => KeyModifiers.None,
        };
    }

    // ------------------------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------------------------

    private void Render()
    {
        if (_coordinator is null) return;
        var s = _coordinator.Snapshot;
        _snapshot = s;

        RenderStatus(s);
        RenderCapture(s);
        RenderListening(s);
        RenderResponse(s);
        RenderAttention(s);
        RenderReview(s);
        RenderInbox(s);
        RenderTasks(s);
        RenderProjects(s);
        RenderRelay(s);
        RenderActivity(s);
        if (DiagnosticsExpander.IsExpanded) RenderDiagnostics();

        // The half-second tick keeps clocks, the buffer meter and live costs moving while anything is in flight.
        var moving = s.State.IsCapturing() || s.State is RelayState.Planning or RelayState.Executing || s.Listening is not null || s.LiveTasks.Any();
        if (moving) { if (!_tick.IsRunning) _tick.Start(); }
        else if (_tick.IsRunning && !DiagnosticsExpander.IsExpanded) _tick.Stop();
    }

    private void RenderStatus(RelaySnapshot s)
    {
        StateLabel.Text = s.State == RelayState.NoteCapture && s.Listening is not null ? "LISTENING" : s.State.Label();
        StateDot.Fill = new SolidColorBrush(StateColor(s.State));
        ModeLabel.Text = s.Mode is { } m ? (m == CaptureMode.Note ? (s.Listening is not null ? "stream" : "silent note") : "instruction") : "";
        StateDetail.Text = StateDetailText(s);

        NoteKeyChip.Text = $"{s.NoteKey.Chord}  {(s.ListeningEnabled ? "LISTEN" : "NOTE")}";
        NoteKeyDot.Fill = new SolidColorBrush(s.NoteKey.Registered ? Palette.Good : Palette.Bad);
        CommandKeyChip.Text = $"{s.CommandKey.Chord}  ASK";
        CommandKeyDot.Fill = new SolidColorBrush(s.CommandKey.Registered ? Palette.Good : Palette.Bad);
        ScopeChip.Text = s.NoteKey.WindowScoped ? "this window only" : "system-wide";
        LedgerChip.Text = s.LedgerHealth == LedgerHealth.IntegrityFailure ? $"Ledger broken · {s.LedgerRecords}" : $"Ledger {s.LedgerRecords}";
        LedgerDot.Fill = new SolidColorBrush(s.LedgerHealth == LedgerHealth.Ok ? Palette.Good : s.LedgerHealth == LedgerHealth.TornTail ? Palette.Warn : Palette.Bad);

        var modelReady = s.ModelEnabled && (s.ModelKeyStored || IsLoopback(s.ModelEndpoint));
        OrchestratorChip.Text = s.OrchestratorMode switch
        {
            OrchestratorSettings.Off => "Planner off",
            OrchestratorSettings.Rules => "Rules only · no model",
            OrchestratorSettings.Mind => s.ModelEnabled ? $"Mind · {s.ModelName}" + (modelReady ? "" : " · NO KEY") : "Mind (model disabled — enable it)",
            _ => s.ModelEnabled ? $"Rules + {s.ModelName}" + (modelReady ? "" : " · NO KEY") : "Rules + model (model disabled)",
        };
        OrchestratorDot.Fill = new SolidColorBrush(s.OrchestratorMode == OrchestratorSettings.Off ? Palette.Neutral
            : s.OrchestratorMode == OrchestratorSettings.Rules || modelReady ? Palette.Good : Palette.Warn);

        // What the note chord does: open a conversation the mind reads, or take one silent note. Without a mind there is nothing to read with.
        ListeningChip.Text = s.ListeningEnabled ? "Listening · read by the mind"
            : s.MindReady ? $"Listening off · {s.NoteKey.Chord} dictates a note"
            : $"No mind · {s.NoteKey.Chord} dictates a note";
        ListeningDot.Fill = new SolidColorBrush(s.ListeningEnabled ? Palette.Good : s.MindReady ? Palette.Neutral : Palette.Warn);

        TitleSubtitle.Text = $"session {Short(s.SessionId)} · pid {s.ProcessId} · v{s.AppVersion}";
        RenderProcessTags(s);
    }

    /// <summary>One chip per lane that is active right now. Each tag names the only permissions that lane may use; nothing else is running.</summary>
    private void RenderProcessTags(RelaySnapshot s)
    {
        var live = s.LiveTasks.OrderByDescending(t => t.Foreground).ThenBy(t => t.StartedAt).ToList();
        var signature = (s.Listening is null ? "" : "L") + string.Join("|", live.Select(t => $"{t.TaskId}:{t.Tag}"));
        ProcessPanel.Visibility = Vis(s.Listening is not null || live.Count > 0);
        if (signature == _processSignature) return;
        _processSignature = signature;

        ProcessTags.Children.Clear();
        if (s.Listening is not null) ProcessTags.Children.Add(ProcessChip("Listening", "the mind reads the buffer · no writes", Palette.Note));
        foreach (var t in live)
        {
            var color = t.Status switch { TaskStatus.AwaitingApproval => Palette.Warn, TaskStatus.Executing => Palette.Good, _ => Palette.Command };
            var who = t.Origin == TaskOrigin.Direct ? (t.Foreground ? "your instruction" : "your ask") : t.Origin == TaskOrigin.Observed ? "observed" : "follow-up";
            ProcessTags.Children.Add(ProcessChip(t.Tag, $"{t.Kind.Wire()} · {who} · {Trim(t.Title ?? t.Instruction, 40)}", color, () => ShowTaskDiagnostics(t.TaskId)));
        }
    }

    private Border ProcessChip(string tag, string detail, Color color, Action? open = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Dot(color));
        var text = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = tag, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + detail, Foreground = Secondary() });
        row.Children.Add(text);
        var chip = new Border { Style = (Style)RootGrid.Resources["Chip"], Child = row };
        if (open is not null)
        {
            ToolTipService.SetToolTip(chip, "Open in Diagnostics");
            chip.Tapped += (_, _) => open();
        }
        return chip;
    }

    private static bool IsLoopback(string? endpoint)
        => endpoint is not null && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host is "localhost");

    private string StateDetailText(RelaySnapshot s)
    {
        var elapsed = s.CaptureStartedAt is { } started ? (DateTimeOffset.UtcNow - started) : TimeSpan.Zero;
        var clock = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        var focus = s.CaptureSurfaceFocused ? "surface focused" : "SURFACE NOT FOCUSED";
        var pending = s.PendingProposals.Count();
        var background = s.LiveTasks.Count(t => !t.Foreground);
        var beside = background > 0 ? $" {background} task(s) running in the background." : "";
        return s.State switch
        {
            RelayState.Starting => "Verifying the ledger and checking for interrupted work…",
            RelayState.Idle => (s.NoteKey.Registered || s.CommandKey.Registered)
                ? (s.ListeningEnabled
                    ? $"Press {s.NoteKey.Chord} to listen or {s.CommandKey.Chord} to give an instruction{(s.NoteKey.WindowScoped ? " while this window is active" : "")}. Nothing is recording."
                    : $"Press {s.NoteKey.Chord} to start a silent note or {s.CommandKey.Chord} to give an instruction{(s.NoteKey.WindowScoped ? " while this window is active" : "")}. Nothing is recording.") + beside
                : "The chords are not active. See Review for the reason and fix hotkeys in settings.json, then restart Relay.",
            RelayState.NoteCapture when s.Listening is { } l =>
                $"Listening · {clock} · {l.HeldSegments} segment(s) held of {l.TotalSegments} heard · {l.Passes} pass(es) · {l.Raised} raised · {focus}. Press {s.NoteKey.Chord} again to stop; Esc cancels. The buffer expires continuously; only excerpts are kept." + beside,
            RelayState.NoteCapture => $"Silent note · {clock} · {s.CaptureChars} chars · {focus}. Press {s.NoteKey.Chord} again to stop; Esc cancels. Relay will not reply.",
            RelayState.CommandCapture => $"Instruction · {clock} · {s.CaptureChars} chars · {focus}. Press {s.CommandKey.Chord} again to stop; Esc cancels.",
            RelayState.AwaitingTranscript => s.Awaiting?.TimedOut == true
                ? $"No transcript arrived within the timeout ({s.CaptureChars} chars present). Retry wait, submit what is here, or cancel."
                : s.Awaiting?.StabilizationPending == true
                    ? $"Text is arriving ({s.CaptureChars} chars). Submitting once it stops changing…"
                    : $"Stop requested at {s.CaptureChars} chars. Waiting for Flow to insert the transcript…",
            RelayState.Organizing => s.Mode == CaptureMode.Note
                ? (s.Listening is { Finishing: true } ? "Final check of what was heard, then the stream closes. Excerpts that were kept stay; the buffer is dropped." : "Storing the capture, extracting notes and routing them to projects. Confident matches are filed; uncertain ones go to Review.")
                : "Storing the instruction verbatim before anything interprets it.",
            RelayState.Planning => $"{s.OrchestratorName} is interpreting the instruction with read-only tools. Nothing changes until you approve. Esc cancels." + beside,
            RelayState.AwaitingApproval => $"{pending} proposal(s) need your decision below. Nothing has changed yet." + beside,
            RelayState.Executing => "Executing approved operation(s) with single-use capabilities. Each write is journaled and versioned." + beside,
            RelayState.Completed => (s.Receipt ?? "Stored.") + beside,
            RelayState.Failed => s.Incident?.Summary ?? "Work stopped without completing.",
            RelayState.Locked => s.Incident?.Summary ?? "Integrity protection stopped the system.",
            _ => "",
        };
    }

    private void RenderCapture(RelaySnapshot s)
    {
        var editable = s.SurfaceEditable;
        var listening = s.State == RelayState.NoteCapture && s.Listening is not null;
        CaptureBox.IsReadOnly = !editable;
        CaptureHeader.Text = listening ? "LISTENING" : "CAPTURE";
        CaptureBox.PlaceholderText = s.State switch
        {
            RelayState.NoteCapture when listening => "Talk with Flow or type. Words land here, are cut into lines and read; the buffer forgets them within the window.",
            RelayState.NoteCapture => "Dictate with Flow or type. Text arrives here and nowhere else.",
            RelayState.CommandCapture => "State your instruction. Relay records it exactly, then plans; nothing runs without approval.",
            RelayState.AwaitingTranscript => "Waiting for the transcript to be inserted…",
            RelayState.Locked => "Locked. Inspect the incident in Review, then unlock.",
            RelayState.Failed => "Stopped. Inspect the failure in Review.",
            _ => s.ListeningEnabled ? $"Press {s.NoteKey.Chord} to listen or {s.CommandKey.Chord} to give an instruction." : $"Press {s.NoteKey.Chord} to start a silent note or {s.CommandKey.Chord} to give an instruction.",
        };

        if (!s.State.IsCapturing() && s.State != RelayState.Organizing && CaptureBox.Text.Length > 0 && s.State is RelayState.Idle or RelayState.Completed or RelayState.Locked)
        {
            _suppressTextChanged = true;
            CaptureBox.Text = "";
            _suppressTextChanged = false;
        }

        ReadyGreeting.Visibility = s.Mode == CaptureMode.Command && s.State is RelayState.CommandCapture or RelayState.AwaitingTranscript ? Visibility.Visible : Visibility.Collapsed;
        FocusBar.IsOpen = s.State.IsCapturing() && !s.CaptureSurfaceFocused;
        CaptureMeta.Text = s.CaptureId is null ? "" : listening ? $"stream {Short(s.CaptureId)}" : $"capture {Short(s.CaptureId)} · {s.CaptureChars} chars";

        CancelButton.Visibility = Vis(s.CanCancel);
        CancelButton.Content = s.State == RelayState.Executing ? "Stop  (Esc)" : listening ? "Discard stream  (Esc)" : "Cancel  (Esc)";
        SubmitNowButton.Visibility = Vis(s.State == RelayState.AwaitingTranscript);
        SubmitNowButton.IsEnabled = s.CanSubmitNow;
        RetryWaitButton.Visibility = Vis(s.CanRetryWait);
        DismissButton.Visibility = Vis(s.State is RelayState.Completed or RelayState.Failed);
        RetryButton.Visibility = Vis(s.CanRetry);
        UnlockButton.Visibility = Vis(s.State == RelayState.Locked);

        // The receipt for a completed capture is shown once, in the status line (StateDetailText).
        NoticeText.Text = s.Notice ?? "";
        NoticeText.Visibility = Vis(!string.IsNullOrEmpty(s.Notice));

        var canAsk = s.CanAsk && s.OrchestratorMode != OrchestratorSettings.Off;
        AskPanel.Visibility = Vis(s.State is not (RelayState.CommandCapture or RelayState.AwaitingTranscript or RelayState.Locked or RelayState.Failed or RelayState.Starting));
        AskBox.IsEnabled = canAsk;
        AskButton.IsEnabled = canAsk;
        AskHint.Text = s.OrchestratorMode == OrchestratorSettings.Off ? "The planner is off; enable it in Settings to ask."
            : listening ? "Ask without stopping the stream: the question runs beside it and the answer arrives as a card in Attention."
            : s.State is RelayState.Idle or RelayState.Completed ? "A direct question or instruction, answered in Response. Nothing runs without approval."
            : "Asked now, the question runs in the background and answers in Attention.";
    }

    /// <summary>The buffer meter and counts while listening: sizes and timings only, never the words.</summary>
    private void RenderListening(RelaySnapshot s)
    {
        var l = s.Listening;
        ListeningPanel.Visibility = Vis(l is not null);
        if (l is null) return;
        var whole = l.WindowSeconds <= 0;   // the whole conversation is held until listening stops
        BufferBar.Maximum = whole ? Math.Max(1, l.HeldSeconds) : Math.Max(1, l.WindowSeconds);
        BufferBar.Value = whole ? l.HeldSeconds : Math.Min(l.HeldSeconds, l.WindowSeconds);
        BufferBar.ShowPaused = l.Finishing;
        BufferText.Text = whole ? $"{l.HeldSeconds:0}s held (whole conversation)" : $"{l.HeldSeconds:0}s of {l.WindowSeconds:0}s held";
        var last = l.LastCheckAt is { } at ? $"last read {(DateTimeOffset.UtcNow - at).TotalSeconds:0}s ago" : "not read yet";
        ListeningText.Text = $"{l.Mind}{(l.Reading ? " is reading now" : $" · {last}")} · {l.Passes} pass(es) · {l.Raised} raised · {l.Excerpts} excerpt(s) kept ({l.RetainedSeconds:0}s retained) · {l.Tasks} task(s) running"
            + (l.Finishing ? " · finishing" : "");
        ListeningError.Visibility = Vis(l.LastError is not null);
        ListeningError.Text = l.LastError is null ? "" : $"Last pass failed: {l.LastError} — that stretch of talk went unread; the next pass starts clean.";
    }

    private void RenderActivity(RelaySnapshot s)
    {
        var added = false;
        foreach (var entry in s.Activity)
        {
            if (entry.Seq <= _lastActivitySeq) continue;
            _activity.Add(new ActivityRow { Time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"), Text = entry.Text, Type = entry.Type });
            _lastActivitySeq = entry.Seq;
            added = true;
        }
        while (_activity.Count > 400) _activity.RemoveAt(0);
        ActivityCount.Text = $"{s.LedgerRecords} records · chain {Short(s.LedgerLastHash)}";
        if (added) ScrollActivityToEnd();
    }

    /// <summary>Deferred so it also works on the first render, before the list has measured its items.</summary>
    private void ScrollActivityToEnd()
        => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (_activity.Count > 0) ActivityList.ScrollIntoView(_activity[^1]); });

    private void RenderDiagnostics()
    {
        if (_coordinator is null || _runtime is null || _snapshot is null) return;
        var s = _snapshot;
        var settings = _coordinator.CurrentSettings;
        var flow = ProcessIdentity.FindProcess(settings.Diagnostics.FlowProcessNames);
        var cost = s.SessionCost;
        DiagnosticsMeta.Text = $"{s.Tasks.Count} task(s) · {cost.TotalTokens} tokens · {cost.ModelCalls} model call(s) · {cost.ToolCalls} tool call(s)";
        RenderTaskDiagnostics(s);

        var rows = new (string Label, string Value, string? OpenPath)[]
        {
            ("Data root", s.DataRootPath, s.DataRootPath),
            ("Ledger", $"{s.LedgerPath}\n{s.LedgerRecords} records · health {s.LedgerHealth} · tail {s.LedgerLastHash}", null),
            ("Session", $"{s.SessionId} · pid {s.ProcessId} · Relay {s.AppVersion}\n{s.Tasks.Count} task(s) this session · {cost.PromptTokens} prompt + {cost.CompletionTokens} completion tokens · {cost.ModelCalls} model call(s) · {cost.ToolCalls} tool call(s)", _runtime.Root.TasksDirectory),
            ("Settings", $"{_runtime.Root.SettingsPath}\nhash {Short(settings.ComputeHash())}" + (_runtime.Settings.Problems.Count > 0 ? $" · {_runtime.Settings.Problems.Count} problem(s) at startup" : ""), _runtime.Root.SettingsPath),
            ("Planner", $"mode {s.OrchestratorMode} · active {s.OrchestratorName}\nplanning timeout {settings.Orchestrator.PlanningTimeoutMs} ms · tool budget {settings.Orchestrator.MaxToolCalls} · auto-route ≥ {settings.Orchestrator.AutoRouteThreshold:0.00} · review ≥ {settings.Orchestrator.ReviewThreshold:0.00}", null),
            ("Listening", settings.Listening.Enabled ? $"on — the note chord opens a conversation{(s.MindReady ? "" : ", but there is no mind to read it")}\npass timeout {settings.Listening.PassTimeoutMs} ms · ≤ {settings.Stream.MaxMovesPerPass} move(s), {settings.Stream.MaxToolCallsPerPass} tool call(s), {settings.Stream.MaxRaisesPerPass} raise(s) per pass" : "off — the note chord dictates one silent note and nothing is read", null),
            ("Stream", $"buffer {s.Preferences.Buffer.TotalSeconds:0}s (settings {settings.Stream.BufferSeconds}s) · segment quiet {settings.Stream.SegmentQuietMs} ms · read every {settings.Stream.ObserveIntervalMs} ms\nexcerpt ≤ {s.Preferences.ExcerptMaxSeconds:0}s · retained ≤ {s.Preferences.MaxRetainedFraction:P0} of elapsed · window file only in staging\\stream", _runtime.Root.ExcerptsDirectory),
            ("Model", s.ModelEnabled ? $"{s.ModelName} at {s.ModelEndpoint}\nkey {(s.ModelKeyStored ? "stored (DPAPI, this account)" : IsLoopback(s.ModelEndpoint) ? "none (loopback)" : "NOT STORED")} · timeout {settings.Model.TimeoutMs} ms · max output {settings.Model.MaxOutputTokens} tokens" : "disabled — no network connection is ever opened", null),
            ("External profiles", s.ExternalProfiles.Count == 0 ? "none — research tasks state the knowledge gap and stop" : string.Join("\n", settings.ExternalModels.Select(p => $"{p.Name}: {p.Model} at {p.Endpoint}{(p.SupportsSearch ? " · search" : "")}")), _runtime.Root.ExternalArtifactsDirectory),
            ("Workers", settings.Workers.Enabled ? $"enabled · {settings.Workers.WallClockSeconds}s wall clock · {settings.Workers.MemoryMb} MB · {settings.Workers.MaxToolCalls} tool calls · job object sandbox" : "disabled (launch_worker is denied by policy)", _runtime.Root.AgentsDirectory),
            ("Hotkeys", $"{s.NoteKey.Name} {s.NoteKey.Chord}: {(s.NoteKey.Registered ? "registered" : "FAILED — " + s.NoteKey.Error)}\n{s.CommandKey.Name} {s.CommandKey.Chord}: {(s.CommandKey.Registered ? "registered" : "FAILED — " + s.CommandKey.Error)}\nscope {(s.NoteKey.WindowScoped ? "this window only" : "system-wide")} · Relay never synthesizes input; start and stop Flow with its own shortcut", null),
            ("Project folders", s.Workspaces.Count == 0 ? "none yet — New project… registers the folder you pick" : string.Join("\n", s.Workspaces.Select(w => w.Path + (w.Present ? "" : "  (missing)"))), _runtime.Root.WorkspacesPath),
            ("Change sets", $"{s.ChangeSets.Count} recent · {s.ChangeSets.Count(c => c.Reverted)} reverted", _runtime.Root.ChangeSetsDirectory),
            ("Flow process", flow.Found ? $"detected: {flow.Name} (pid {flow.ProcessId})" : $"not detected (looking for {string.Join(", ", settings.Diagnostics.FlowProcessNames)})", null),
            ("Foreground", $"{ForegroundWindows.ForegroundProcessName() ?? "?"} · Relay is foreground: {ForegroundWindows.IsForeground(_hwnd)} · capture surface focused: {s.CaptureSurfaceFocused}", null),
            ("Timeouts", $"transcript {settings.Capture.TranscriptTimeoutMs} ms · quiet {settings.Capture.StabilizationMs} ms · draft debounce {settings.Capture.DraftPersistDebounceMs} ms", null),
            ("Window", $"hwnd 0x{_hwnd.ToInt64():X} · scale {WindowMetrics.ScaleFor(_hwnd):0.00}", null),
        };

        FillRows(DiagnosticsGrid, rows);
    }

    private void FillRows(Grid grid, IReadOnlyList<(string Label, string Value, string? OpenPath)> rows)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, FontSize = 12, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            var valuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            valuePanel.Children.Add(new TextBlock { Text = rows[i].Value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 420 });
            if (rows[i].OpenPath is { } path)
            {
                var open = Button("Open", () => OpenInExplorer(path));
                open.Padding = new Thickness(8, 2, 8, 2);
                open.FontSize = 11;
                valuePanel.Children.Add(open);
            }
            Grid.SetRow(valuePanel, i);
            Grid.SetColumn(valuePanel, 1);
            grid.Children.Add(valuePanel);
        }
    }

    // ------------------------------------------------------------------------------------
    // Event handlers
    // ------------------------------------------------------------------------------------

    private void CaptureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        _coordinator?.TextChanged(CaptureBox.Text);
    }

    private void CaptureBox_GotFocus(object sender, RoutedEventArgs e) => _coordinator?.FocusChanged(ForegroundWindows.IsForeground(_hwnd));
    private void CaptureBox_LostFocus(object sender, RoutedEventArgs e) => _coordinator?.FocusChanged(false);

    private void CaptureBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _snapshot?.CanCancel == true)
        {
            e.Handled = true;
            _coordinator?.Cancel();
        }
    }

    private void AskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            SubmitAsk();
        }
    }

    private void Ask_Click(object sender, RoutedEventArgs e) => SubmitAsk();

    /// <summary>The ask box submits a direct task. While listening it runs beside the stream; the capture surface keeps the focus it had.</summary>
    private void SubmitAsk()
    {
        if (_coordinator is null) return;
        var text = AskBox.Text.Trim();
        if (text.Length == 0) return;
        if (_coordinator.SubmitDirect(text))
        {
            AskBox.Text = "";
            if (_snapshot?.State == RelayState.NoteCapture) CaptureBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_coordinator is null) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _coordinator.FocusChanged(false);
        }
        else
        {
            _coordinator.FocusChanged(CaptureBoxHasFocus());
        }
    }

    private void Refocus_Click(object sender, RoutedEventArgs e)
    {
        BringForward();
        CaptureBox.Focus(FocusState.Programmatic);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _coordinator?.Cancel();
    private void SubmitNow_Click(object sender, RoutedEventArgs e) => _coordinator?.SubmitNow();
    private void RetryWait_Click(object sender, RoutedEventArgs e) => _coordinator?.RetryWait();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => _coordinator?.Dismiss();
    private void Retry_Click(object sender, RoutedEventArgs e) => _coordinator?.Retry();
    private void Unlock_Click(object sender, RoutedEventArgs e) => _coordinator?.Unlock();
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsDialogAsync();
    private async void NewProject_Click(object sender, RoutedEventArgs e) => await ShowNewProjectDialogAsync();
    private void Backup_Click(object sender, RoutedEventArgs e) => _coordinator?.ExportBackup();

    private void ActivityList_Loaded(object sender, RoutedEventArgs e) => ScrollActivityToEnd();

    private void Diagnostics_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        RenderDiagnostics();
        if (!_tick.IsRunning) _tick.Start();
    }

    private void CloseTaskDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsTaskId = null;
        _taskDiagnosticsSignature = "";
        TaskDiagnosticsPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenTaskFile_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _diagnosticsTaskId is null) return;
        var path = Path.Combine(_runtime.Root.TasksDirectory, _diagnosticsTaskId + ".json");
        OpenInExplorer(File.Exists(path) ? path : _runtime.Root.TasksDirectory);
    }

    /// <summary>Opens the diagnostics drawer on one task and scrolls it into view.</summary>
    private void ShowTaskDiagnostics(string taskId)
    {
        _diagnosticsTaskId = taskId;
        _taskDiagnosticsSignature = "";
        DiagnosticsExpander.IsExpanded = true;
        RenderDiagnostics();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => DiagnosticsExpander.StartBringIntoView());
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Microsoft.UI.Xaml.Shapes.Ellipse Dot(Color color, double size = 6)
        => new() { Width = size, Height = size, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Focus check that does not depend on XamlRoot, which is not yet available during the first activation.</summary>
    private bool CaptureBoxHasFocus() => CaptureBox.FocusState != FocusState.Unfocused;

    private static string Short(string? value) => value is null ? "?" : value.Length > 10 ? value[^8..] : value;

    private static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Button Button(string text, Action action, bool accent = false, bool enabled = true, bool small = false)
    {
        var button = new Button { Content = text, IsEnabled = enabled };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (small) { button.Padding = new Thickness(10, 4, 10, 4); button.FontSize = 12; button.MinHeight = 0; }
        button.Click += (_, _) => action();
        return button;
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience; failures are not part of the record.
        }
    }

    private static Color StateColor(RelayState state) => state switch
    {
        RelayState.NoteCapture => Palette.Note,
        RelayState.CommandCapture or RelayState.Planning => Palette.Command,
        RelayState.AwaitingTranscript or RelayState.Organizing or RelayState.AwaitingApproval or RelayState.Executing => Palette.Warn,
        RelayState.Completed => Palette.Good,
        RelayState.Failed or RelayState.Locked => Palette.Bad,
        _ => Palette.Neutral,
    };

    /// <summary>Indicator colours tuned for the dark surface: saturated enough to read at 6–10 px.</summary>
    private static class Palette
    {
        public static readonly Color Neutral = Color.FromArgb(255, 128, 128, 136);
        public static readonly Color Note = Color.FromArgb(255, 45, 212, 191);
        public static readonly Color Command = Color.FromArgb(255, 129, 140, 248);
        public static readonly Color Warn = Color.FromArgb(255, 251, 191, 36);
        public static readonly Color Good = Color.FromArgb(255, 74, 222, 128);
        public static readonly Color Bad = Color.FromArgb(255, 248, 113, 113);
    }

    /// <summary>The coordinator's view of this window. Only the capture surface is ever touched.</summary>
    private sealed class Host : ICaptureHost
    {
        private readonly MainWindow _w;
        public Host(MainWindow w) => _w = w;

        public void PrepareCaptureSurface()
        {
            _w._suppressTextChanged = true;
            _w.CaptureBox.Text = "";
            _w._suppressTextChanged = false;
            _w.CaptureBox.IsReadOnly = false;
            ForegroundWindows.BringToForeground(_w._hwnd);
            _w.CaptureBox.Focus(FocusState.Programmatic);
        }

        public string? ForegroundProcessName() => ForegroundWindows.ForegroundProcessName();
    }
}
