using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Relay.Core.Config;
using Relay.Core.Input;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Windows;

namespace Relay.Desktop;

public partial class App : Application
{
    private const int NoteHotkeyId = 1;
    private const int CommandHotkeyId = 2;

    private SingleInstance? _instance;
    private CaseRelayHost? _host;
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
            _instance.Dispose();
            Exit();
            return;
        }

        try
        {
            _window = new MainWindow();
            _host = CaseRelayHost.Create(root, SystemClock.Instance, Version);
        }
        catch (Exception ex)
        {
            WriteStartupIncident(root, ex);
            _window ??= new MainWindow();
            _window.ShowStartupFailure(root, ex);
            _window.Activate();
            return;
        }

        _window.AttachSurface(_host);
        _window.Closed += OnWindowClosed;
        _instance.ActivationRequested += () => _dispatcher.TryEnqueue(() => _window.BringForward());

        RegisterHotkeys(_host.Settings.Hotkeys);

        _window.Activate();
    }

    private void RegisterHotkeys(HotkeySettings settings)
    {
        if (settings.IsWindowScoped)
        {
            var noteOk = KeyChord.TryParse(settings.NoteKey, allowModifierOnly: true, out var note, out _);
            var commandOk = KeyChord.TryParse(settings.CommandKey, allowModifierOnly: true, out var command, out _);
            _window!.SetWindowChords(
                noteOk ? note : null,
                commandOk ? command : null,
                () => _host?.Surface.ToggleListening(),
                () => { /* ask focus handled in window */ });
            return;
        }

        try
        {
            _hotkeys = new HotkeyListener();
        }
        catch
        {
            return;
        }

        _hotkeys.Pressed += id => _dispatcher!.TryEnqueue(() =>
        {
            if (id == NoteHotkeyId) _host?.Surface.ToggleListening();
        });

        if (KeyChord.TryParse(settings.NoteKey, out var noteChord, out _))
            _hotkeys.Register(NoteHotkeyId, noteChord);
        if (KeyChord.TryParse(settings.CommandKey, out var cmdChord, out _))
            _hotkeys.Register(CommandHotkeyId, cmdChord);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _host?.Dispose();
        }
        finally
        {
            _hotkeys?.Dispose();
            _instance?.Dispose();
        }
    }

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_handlingFailure || _host is null) return;
        _handlingFailure = true;
        try
        {
            e.Handled = true;
            WriteStartupIncident(_host.Root, e.Exception);
        }
        catch { /* best effort */ }
        finally { _handlingFailure = false; }
    }

    private void OnDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex && _host is not null)
            WriteStartupIncident(_host.Root, ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (_host is not null) WriteStartupIncident(_host.Root, e.Exception);
        e.SetObserved();
    }

    private static void WriteStartupIncident(DataRoot root, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(root.IncidentsDirectory);
            var path = Path.Combine(root.IncidentsDirectory, $"startup-{DateTime.UtcNow:yyyyMMddTHHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow,
                error = ex.ToString(),
            }, RelayJson.Indented));
        }
        catch { /* best effort */ }
    }
}
