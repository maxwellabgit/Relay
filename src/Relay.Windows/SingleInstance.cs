using System.Security.Cryptography;
using System.Text;

namespace Relay.Windows;

/// <summary>
/// One Relay per data root. The second instance signals the first to show itself and exits;
/// the ledger's exclusive writer handle is the backstop if this guard is ever bypassed.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly bool _owned;
    private Thread? _watcher;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool owned)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = owned;
    }

    public bool IsFirstInstance => _owned;

    /// <summary>Raised on a background thread when another instance asked this one to come forward.</summary>
    public event Action? ActivationRequested;

    public static SingleInstance Acquire(string dataRootPath)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dataRootPath.ToUpperInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: false, $"Local\\Relay.Instance.{key}", out _);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\Relay.Activate.{key}");

        bool owned;
        try { owned = mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { owned = true; }

        var instance = new SingleInstance(mutex, activate, owned);
        if (owned) instance.StartWatcher();
        else activate.Set();
        return instance;
    }

    private void StartWatcher()
    {
        _watcher = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_activate.WaitOne(500)) ActivationRequested?.Invoke();
                }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "Relay.SingleInstance" };
        _watcher.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
