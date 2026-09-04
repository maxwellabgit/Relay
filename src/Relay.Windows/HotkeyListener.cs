using System.ComponentModel;
using System.Runtime.InteropServices;
using Relay.Core.Input;
using static Relay.Windows.NativeMethods;

namespace Relay.Windows;

public sealed record HotkeyRegistration(int Id, KeyChord Chord, bool Registered, string? Error);

/// <summary>
/// Global hotkeys via <c>RegisterHotKey</c> on a message-only window owned by a dedicated
/// thread. No low-level keyboard hook is installed (contract §14): Windows only tells Relay
/// about the exact chords it registered. Presses are raised on the listener thread; the host
/// marshals them to its UI thread.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const uint WM_APP_REGISTER = WM_APP + 1;
    private const uint WM_APP_UNREGISTER = WM_APP + 2;
    private const uint WM_APP_QUIT = WM_APP + 3;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<int, KeyChord> _pending = new();
    private readonly object _gate = new();
    private readonly WndProc _wndProc; // kept alive for the unmanaged callback
    private IntPtr _hwnd;
    private Exception? _startupError;
    private bool _disposed;

    public HotkeyListener()
    {
        _wndProc = WindowProcedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "Relay.HotkeyListener" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null) throw _startupError;
    }

    /// <summary>Raised on the listener thread with the registration id of the pressed hotkey.</summary>
    public event Action<int>? Pressed;

    public HotkeyRegistration Register(int id, KeyChord chord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate) _pending[id] = chord;
        var result = SendMessageW(_hwnd, WM_APP_REGISTER, (IntPtr)id, IntPtr.Zero).ToInt64();
        if (result == 0) return new HotkeyRegistration(id, chord, true, null);
        var error = result == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "another application already owns this key combination"
            : new Win32Exception((int)result).Message;
        return new HotkeyRegistration(id, chord, false, error);
    }

    public void Unregister(int id)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        SendMessageW(_hwnd, WM_APP_UNREGISTER, (IntPtr)id, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            var className = "Relay.HotkeyWindow." + Environment.ProcessId;
            var classNamePtr = Marshal.StringToHGlobalUni(className);
            try
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandleW(IntPtr.Zero),
                    lpszClassName = classNamePtr,
                };
                if (RegisterClassExW(ref wc) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed for the hotkey window");
                }
                _hwnd = CreateWindowExW(0, className, "Relay hotkeys", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for the hotkey window");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                Pressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;

            case WM_APP_REGISTER:
            {
                var id = wParam.ToInt32();
                KeyChord? chord;
                lock (_gate) _pending.TryGetValue(id, out chord);
                if (chord is null) return (IntPtr)87; // ERROR_INVALID_PARAMETER
                var modifiers = ToNativeModifiers(chord.Modifiers) | MOD_NOREPEAT;
                if (RegisterHotKey(hWnd, id, modifiers, chord.VirtualKey)) return IntPtr.Zero;
                return (IntPtr)Marshal.GetLastWin32Error();
            }

            case WM_APP_UNREGISTER:
                UnregisterHotKey(hWnd, wParam.ToInt32());
                return IntPtr.Zero;

            case WM_APP_QUIT:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static uint ToNativeModifiers(KeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= MOD_ALT;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= MOD_CONTROL;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= MOD_SHIFT;
        if (modifiers.HasFlag(KeyModifiers.Win)) result |= MOD_WIN;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            lock (_gate)
            {
                foreach (var id in _pending.Keys) SendMessageW(_hwnd, WM_APP_UNREGISTER, (IntPtr)id, IntPtr.Zero);
            }
            SendMessageW(_hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
