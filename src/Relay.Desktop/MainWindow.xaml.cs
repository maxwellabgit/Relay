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
using Relay.Core.Ledger;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Windows;
using Windows.UI;

namespace Relay.Desktop;

public sealed class ActivityRow
{
    public required string Time { get; init; }
    public required string Text { get; init; }
    public required string Type { get; init; }
}

/// <summary>
/// The single window. It renders the coordinator's snapshot and forwards user decisions; it holds
/// no state of its own beyond render caches. Every region maps to a contract region: status,
/// capture, response, review, projects, activity, diagnostics.
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
    private string _projectsSignature = "";
    private RelaySnapshot? _snapshot;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CaptureHost = new Host(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Title = "Relay";

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
        _tick.Tick += (_, _) => { if (_snapshot is not null) RenderStatus(_snapshot); if (DiagnosticsExpander.IsExpanded) RenderDiagnostics(); };
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
        RenderResponse(s);
        RenderReview(s);
        RenderProjects(s);
        RenderActivity(s);
        if (DiagnosticsExpander.IsExpanded) RenderDiagnostics();

        if (s.State.IsCapturing() || s.State is RelayState.Planning or RelayState.Executing) { if (!_tick.IsRunning) _tick.Start(); }
        else if (_tick.IsRunning && !DiagnosticsExpander.IsExpanded) _tick.Stop();
    }

    private void RenderStatus(RelaySnapshot s)
    {
        StateLabel.Text = s.State.Label();
        StateDot.Fill = new SolidColorBrush(StateColor(s.State));
        ModeLabel.Text = s.Mode is { } m ? (m == CaptureMode.Note ? "silent note" : "instruction") : "";
        StateDetail.Text = StateDetailText(s);

        NoteKeyChip.Text = $"{s.NoteKey.Chord}  NOTE";
        NoteKeyDot.Fill = new SolidColorBrush(s.NoteKey.Registered ? Palette.Good : Palette.Bad);
        CommandKeyChip.Text = $"{s.CommandKey.Chord}  COMMAND";
        CommandKeyDot.Fill = new SolidColorBrush(s.CommandKey.Registered ? Palette.Good : Palette.Bad);
        RelayChip.Text = s.FlowRelayEnabled ? $"Flow relay {s.FlowRelayChord}" : "Flow relay off";
        LedgerChip.Text = s.LedgerHealth == LedgerHealth.IntegrityFailure ? $"Ledger broken · {s.LedgerRecords}" : $"Ledger {s.LedgerRecords}";
        LedgerDot.Fill = new SolidColorBrush(s.LedgerHealth == LedgerHealth.Ok ? Palette.Good : s.LedgerHealth == LedgerHealth.TornTail ? Palette.Warn : Palette.Bad);

        var modelReady = s.ModelEnabled && s.ModelKeyStored;
        OrchestratorChip.Text = s.OrchestratorMode switch
        {
            OrchestratorSettings.Off => "Orchestrator off",
            OrchestratorSettings.Rules => "Rules only · no model",
            _ => s.ModelEnabled ? $"Rules + {s.ModelName}" + (s.ModelKeyStored ? "" : " · NO KEY") : "Rules + model (model disabled)",
        };
        OrchestratorDot.Fill = new SolidColorBrush(s.OrchestratorMode == OrchestratorSettings.Off ? Palette.Neutral
            : s.OrchestratorMode == OrchestratorSettings.Rules || modelReady ? Palette.Good : Palette.Warn);
        TitleSubtitle.Text = $"session {Short(s.SessionId)} · pid {s.ProcessId} · v{s.AppVersion}";
    }

    private string StateDetailText(RelaySnapshot s)
    {
        var elapsed = s.CaptureStartedAt is { } started ? (DateTimeOffset.UtcNow - started) : TimeSpan.Zero;
        var clock = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        var focus = s.CaptureSurfaceFocused ? "surface focused" : "SURFACE NOT FOCUSED";
        var pending = s.PendingProposals.Count();
        return s.State switch
        {
            RelayState.Starting => "Verifying the ledger and checking for interrupted work…",
            RelayState.Idle => (s.NoteKey.Registered || s.CommandKey.Registered)
                ? $"Press {s.NoteKey.Chord} to start a silent note or {s.CommandKey.Chord} to give an instruction. Nothing is recording."
                : "Hotkeys are not active. See Review for the reason. You can still type a note or an instruction below.",
            RelayState.NoteCapture => $"Silent note · {clock} · {s.CaptureChars} chars · {focus}. Press {s.NoteKey.Chord} again to stop; Esc cancels. Relay will not reply.",
            RelayState.CommandCapture => $"Instruction · {clock} · {s.CaptureChars} chars · {focus}. Press {s.CommandKey.Chord} again to stop; Esc cancels.",
            RelayState.AwaitingTranscript => s.Awaiting?.TimedOut == true
                ? $"No transcript arrived within the timeout ({s.CaptureChars} chars present). Retry wait, submit what is here, or cancel."
                : s.Awaiting?.StabilizationPending == true
                    ? $"Text is arriving ({s.CaptureChars} chars). Submitting once it stops changing…"
                    : $"Stop requested at {s.CaptureChars} chars. Waiting for Flow to insert the transcript…",
            RelayState.Organizing => s.Mode == CaptureMode.Note
                ? "Storing the capture, extracting notes and routing them to projects. Confident matches are filed; uncertain ones go to Review."
                : "Storing the instruction verbatim before anything interprets it.",
            RelayState.Planning => $"{s.OrchestratorName} is interpreting the instruction with read-only tools. Nothing changes until you approve. Esc cancels.",
            RelayState.AwaitingApproval => $"{pending} proposal(s) need your decision below. Nothing has changed yet.",
            RelayState.Executing => "Executing approved operation(s) with single-use capabilities. Each write is journaled and versioned.",
            RelayState.Completed => s.Receipt ?? "Stored.",
            RelayState.Failed => s.Incident?.Summary ?? "Work stopped without completing.",
            RelayState.Locked => s.Incident?.Summary ?? "Integrity protection stopped the system.",
            _ => "",
        };
    }

    private void RenderCapture(RelaySnapshot s)
    {
        var editable = s.SurfaceEditable;
        CaptureBox.IsReadOnly = !editable;
        CaptureBox.PlaceholderText = s.State switch
        {
            RelayState.NoteCapture => "Dictate with Flow or type. Text arrives here and nowhere else.",
            RelayState.CommandCapture => "State your instruction. Relay records it exactly, then plans; nothing runs without approval.",
            RelayState.AwaitingTranscript => "Waiting for the transcript to be inserted…",
            RelayState.Locked => "Locked. Inspect the incident in Review, then unlock.",
            RelayState.Failed => "Stopped. Inspect the failure in Review.",
            _ => $"Press {s.NoteKey.Chord} to start a silent note or {s.CommandKey.Chord} to give an instruction.",
        };

        if (!s.State.IsCapturing() && s.State != RelayState.Organizing && CaptureBox.Text.Length > 0 && s.State is RelayState.Idle or RelayState.Completed or RelayState.Locked)
        {
            _suppressTextChanged = true;
            CaptureBox.Text = "";
            _suppressTextChanged = false;
        }

        ReadyGreeting.Visibility = s.Mode == CaptureMode.Command && s.State is RelayState.CommandCapture or RelayState.AwaitingTranscript ? Visibility.Visible : Visibility.Collapsed;
        FocusBar.IsOpen = s.State.IsCapturing() && !s.CaptureSurfaceFocused;
        CaptureMeta.Text = s.CaptureId is null ? "" : $"capture {Short(s.CaptureId)} · {s.CaptureChars} chars";

        CancelButton.Visibility = Vis(s.CanCancel);
        CancelButton.Content = s.State == RelayState.Executing ? "Stop  (Esc)" : "Cancel  (Esc)";
        SubmitNowButton.Visibility = Vis(s.State == RelayState.AwaitingTranscript);
        SubmitNowButton.IsEnabled = s.CanSubmitNow;
        RetryWaitButton.Visibility = Vis(s.CanRetryWait);
        DismissButton.Visibility = Vis(s.State is RelayState.Completed or RelayState.Failed);
        RetryButton.Visibility = Vis(s.CanRetry);
        UnlockButton.Visibility = Vis(s.State == RelayState.Locked);

        // Keyboard-only path: the same transitions as the hotkeys, for machines without F13/F14 or without Flow.
        TypeInstructionButton.Visibility = Vis(s.State is RelayState.Idle or RelayState.Completed or RelayState.CommandCapture);
        TypeInstructionButton.Content = s.State == RelayState.CommandCapture ? "Send instruction" : "Type an instruction";
        TypeNoteButton.Visibility = Vis(s.State is RelayState.Idle or RelayState.Completed or RelayState.NoteCapture);
        TypeNoteButton.Content = s.State == RelayState.NoteCapture ? "Finish note" : "Type a note";

        NoticeText.Text = s.Notice ?? "";
        NoticeText.Visibility = Vis(!string.IsNullOrEmpty(s.Notice));
        ReceiptText.Text = s.Receipt ?? "";
        ReceiptText.Visibility = Vis(s.State == RelayState.Completed && !string.IsNullOrEmpty(s.Receipt));
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
        var rows = new (string Label, string Value, string? OpenPath)[]
        {
            ("Data root", s.DataRootPath, s.DataRootPath),
            ("Ledger", $"{s.LedgerPath}\n{s.LedgerRecords} records · health {s.LedgerHealth} · tail {s.LedgerLastHash}", null),
            ("Session", $"{s.SessionId} · pid {s.ProcessId} · Relay {s.AppVersion}", null),
            ("Settings", $"{_runtime.Root.SettingsPath}\nhash {Short(settings.ComputeHash())}" + (_runtime.Settings.Problems.Count > 0 ? $" · {_runtime.Settings.Problems.Count} problem(s) at startup" : ""), _runtime.Root.SettingsPath),
            ("Orchestrator", $"mode {s.OrchestratorMode} · active {s.OrchestratorName}\nplanning timeout {settings.Orchestrator.PlanningTimeoutMs} ms · tool budget {settings.Orchestrator.MaxToolCalls} · auto-route ≥ {settings.Orchestrator.AutoRouteThreshold:0.00} · review ≥ {settings.Orchestrator.ReviewThreshold:0.00}", null),
            ("Model", s.ModelEnabled ? $"{s.ModelName} at {s.ModelEndpoint}\nkey {(s.ModelKeyStored ? "stored (DPAPI, this account)" : "NOT STORED")} · timeout {settings.Model.TimeoutMs} ms · max output {settings.Model.MaxOutputTokens} tokens" : "disabled — no network connection is ever opened", null),
            ("Workers", settings.Workers.Enabled ? $"enabled · {settings.Workers.WallClockSeconds}s wall clock · {settings.Workers.MemoryMb} MB · {settings.Workers.MaxToolCalls} tool calls · job object sandbox" : "disabled (launch_worker is denied by policy)", _runtime.Root.AgentsDirectory),
            ("Hotkeys", $"NOTE_KEY {s.NoteKey.Chord}: {(s.NoteKey.Registered ? "registered" : "FAILED — " + s.NoteKey.Error)}\nCOMMAND_KEY {s.CommandKey.Chord}: {(s.CommandKey.Registered ? "registered" : "FAILED — " + s.CommandKey.Error)}", null),
            ("Flow relay", s.FlowRelayEnabled ? $"enabled · emits only {s.FlowRelayChord} · after {settings.FlowRelay.StartDelayMs} ms" : "disabled (flowRelay.enabled = false). Trigger Flow with its own hotkey.", null),
            ("Flow process", flow.Found ? $"detected: {flow.Name} (pid {flow.ProcessId})" : $"not detected (looking for {string.Join(", ", settings.Diagnostics.FlowProcessNames)})", null),
            ("Foreground", $"{ForegroundWindows.ForegroundProcessName() ?? "?"} · Relay is foreground: {ForegroundWindows.IsForeground(_hwnd)} · capture surface focused: {s.CaptureSurfaceFocused}", null),
            ("Timeouts", $"transcript {settings.Capture.TranscriptTimeoutMs} ms · quiet {settings.Capture.StabilizationMs}/{settings.Capture.StabilizationWithoutRelayMs} ms · draft debounce {settings.Capture.DraftPersistDebounceMs} ms", null),
            ("Window", $"hwnd 0x{_hwnd.ToInt64():X} · scale {WindowMetrics.ScaleFor(_hwnd):0.00}", null),
        };

        DiagnosticsGrid.Children.Clear();
        DiagnosticsGrid.RowDefinitions.Clear();
        for (var i = 0; i < rows.Length; i++)
        {
            DiagnosticsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, FontSize = 12, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(label, i);
            DiagnosticsGrid.Children.Add(label);

            var valuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            valuePanel.Children.Add(new TextBlock { Text = rows[i].Value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 400 });
            if (rows[i].OpenPath is { } path)
            {
                var open = Button("Open", () => OpenInExplorer(path));
                open.Padding = new Thickness(8, 2, 8, 2);
                open.FontSize = 11;
                valuePanel.Children.Add(open);
            }
            Grid.SetRow(valuePanel, i);
            Grid.SetColumn(valuePanel, 1);
            DiagnosticsGrid.Children.Add(valuePanel);
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
        if (e.Key == global::Windows.System.VirtualKey.Escape && _snapshot?.CanCancel == true)
        {
            e.Handled = true;
            _coordinator?.Cancel();
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
    private void TypeInstruction_Click(object sender, RoutedEventArgs e) => _coordinator?.PressCommandKey();
    private void TypeNote_Click(object sender, RoutedEventArgs e) => _coordinator?.PressNoteKey();
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsDialogAsync();
    private async void NewProject_Click(object sender, RoutedEventArgs e) => await ShowNewProjectDialogAsync();
    private async void AddWorkspace_Click(object sender, RoutedEventArgs e) => await ShowAddWorkspaceDialogAsync();
    private void Backup_Click(object sender, RoutedEventArgs e) => _coordinator?.ExportBackup();

    private void ActivityList_Loaded(object sender, RoutedEventArgs e) => ScrollActivityToEnd();

    private void Diagnostics_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        RenderDiagnostics();
        if (!_tick.IsRunning) _tick.Start();
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

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

    private static class Palette
    {
        public static readonly Color Neutral = Color.FromArgb(255, 138, 138, 138);
        public static readonly Color Note = Color.FromArgb(255, 15, 123, 108);
        public static readonly Color Command = Color.FromArgb(255, 91, 95, 199);
        public static readonly Color Warn = Color.FromArgb(255, 193, 156, 0);
        public static readonly Color Good = Color.FromArgb(255, 15, 123, 15);
        public static readonly Color Bad = Color.FromArgb(255, 196, 43, 28);
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

        public bool IsCaptureSurfaceForeground()
            => ForegroundWindows.IsForeground(_w._hwnd) && _w.CaptureBoxHasFocus();

        public string? ForegroundProcessName() => ForegroundWindows.ForegroundProcessName();
    }
}
