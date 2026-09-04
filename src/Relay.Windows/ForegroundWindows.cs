using System.Diagnostics;
using static Relay.Windows.NativeMethods;

namespace Relay.Windows;

/// <summary>Foreground-window queries and control. Window titles are deliberately never read.</summary>
public static class ForegroundWindows
{
    public static IntPtr Current() => GetForegroundWindow();

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>Process name (without extension) that owns the foreground window, or null.</summary>
    public static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Restores and activates the window. Permitted because Relay received the hotkey input.</summary>
    public static bool BringToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);
        return SetForegroundWindow(hwnd);
    }
}

public static class WindowMetrics
{
    /// <summary>Scale factor (1.0 = 96 DPI) for sizing a window in device pixels.</summary>
    public static double ScaleFor(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}

public static class ProcessIdentity
{
    public static int CurrentProcessId => Environment.ProcessId;

    /// <summary>True when any running process matches one of the configured names (case-insensitive).</summary>
    public static (bool Found, int? ProcessId, string? Name) FindProcess(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (set.Contains(process.ProcessName)) return (true, process.Id, process.ProcessName);
            }
            finally
            {
                process.Dispose();
            }
        }
        return (false, null, null);
    }
}
