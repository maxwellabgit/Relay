using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Relay.Core.Config;
using Relay.Core.Input;
using Relay.Core.Session;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Windows;

namespace Relay.Desktop;

public partial class App : Application
{
    private const int NoteHotkeyId = 1;
    private const int CommandHotkeyId = 2;

    private SingleInstance? _instance;
    private RelayRuntime? _runtime;
    private HotkeyListener? _hotkeys;
    private MainWindow? _window;
    private DispatcherQueue? _dispatcher;
    private bool _handlingFailure;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var root = DataRoot.Resolve();

        _instance = SingleInstance.Acquire(root.Path);
        if (!_instance.IsFirstInstance)
        {
            // The running instance was signalled to come forward; this one has nothing to own.
            _instance.Dispose();
            Exit();
            return;
        }

        try
        {
            _window = new MainWindow();
            var host = _window.CaptureHost;
            var options = new RuntimeOptions
            {
                RelayFactory = settings => settings.FlowRelay.Enabled ? new FixedChordRelay(KeyChord.Parse(settings.FlowRelay.HandsFreeChord)) : DisabledFlowRelay.Instance,
                WorkerHostFactory = settings =>
                {
                    var worker = Relay.Core.Agents.ProcessWorkerHost.Locate(settings.Workers.Executable, AppContext.BaseDirectory);
                    return worker is null ? null : new JobObjectWorkerHost(worker);
                },
                ModelOrchestratorFactory = settings => ModelComposition.Create(settings, root),
                Secrets = ModelComposition.Secrets(root),
            };
            _runtime = RelayRuntime.Create(
                root,
                host,
                options,
                SystemClock.Instance,
                new DispatcherScheduler(_dispatcher),
                Version,
                ProcessIdentity.CurrentProcessId);
        }
        catch (Exception ex)
        {
            WriteStartupIncident(root, ex);
            _window ??= new MainWindow();
            _window.ShowStartupFailure(root, ex);
            _window.Activate();
            return;
        }

        var coordinator = _runtime.Coordinator;
        _window.Attach(coordinator, _runtime);
        _window.Closed += OnWindowClosed;
        _instance.ActivationRequested += () => _dispatcher.TryEnqueue(() => _window.BringForward());

        _runtime.Start();

        var acl = DirectoryAcl.Harden(root.Path);
        coordinator.ReportStorageAcl(acl.Applied, acl.Error);

        RegisterHotkeys(coordinator, _runtime.Settings.Settings);

        _window.Activate();
    }

    private void RegisterHotkeys(SessionCoordinator coordinator, RelaySettings settings)
    {
        try
        {
            _hotkeys = new HotkeyListener();
        }
        catch (Exception ex)
        {
            coordinator.ReportHotkey("NOTE_KEY", settings.Hotkeys.NoteKey, false, "hotkey listener failed: " + ex.Message);
            coordinator.ReportHotkey("COMMAND_KEY", settings.Hotkeys.CommandKey, false, "hotkey listener failed: " + ex.Message);
            return;
        }

        _hotkeys.Pressed += id => _dispatcher!.TryEnqueue(() =>
        {
            if (id == NoteHotkeyId) coordinator.PressNoteKey();
            else if (id == CommandHotkeyId) coordinator.PressCommandKey();
        });

        Register("NOTE_KEY", NoteHotkeyId, settings.Hotkeys.NoteKey);
        Register("COMMAND_KEY", CommandHotkeyId, settings.Hotkeys.CommandKey);

        void Register(string name, int id, string chordText)
        {
            if (!KeyChord.TryParse(chordText, out var chord, out var error))
            {
                coordinator.ReportHotkey(name, chordText, false, error);
                return;
            }
            var result = _hotkeys.Register(id, chord);
            coordinator.ReportHotkey(name, chord.ToString(), result.Registered, result.Error);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _runtime?.Coordinator.Shutdown("user_exit");
        }
        finally
        {
            _hotkeys?.Dispose();
            _runtime?.Dispose();
            _instance?.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------
    // Failure handling: nothing is swallowed silently. Each path records the exception in the
    // ledger (when it can be written) and an incident file, then either keeps the UI alive in
    // FAILED or, for a dying process, flushes what it can.
    // ------------------------------------------------------------------------------------

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_handlingFailure || _runtime is null)
        {
            return; // let it terminate; the domain handler records it
        }
        _handlingFailure = true;
        try
        {
            e.Handled = true;
            _runtime.Coordinator.ReportFailure(e.Exception, "ui");
        }
        catch
        {
            e.Handled = false;
        }
        finally
        {
            _handlingFailure = false;
        }
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown");
        try
        {
            if (_runtime is not null)
            {
                _runtime.Coordinator.ReportFailure(exception, "process");
                _runtime.Coordinator.Shutdown("crash");
                _runtime.Dispose();
            }
            else
            {
                WriteStartupIncident(DataRoot.Resolve(), exception);
            }
        }
        catch
        {
            // The process is terminating; there is nothing further to do safely.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var exception = e.Exception;
        _dispatcher?.TryEnqueue(() => _runtime?.Coordinator.ReportFailure(exception, "background task"));
    }

    private static void WriteStartupIncident(DataRoot root, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(root.IncidentsDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
            var payload = new { kind = "startup_failed", at = DateTimeOffset.UtcNow, exceptionType = ex.GetType().FullName, message = ex.Message, detail = ex.ToString(), pid = Environment.ProcessId };
            AtomicFile.WriteAllText(Path.Combine(root.IncidentsDirectory, $"{stamp}-startup_failed.json"), JsonSerializer.Serialize(payload, RelayJson.Indented));
        }
        catch
        {
            // Nowhere left to record it.
        }
    }
}
