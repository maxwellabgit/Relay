namespace Relay.Core.Cases;

/// <summary>
/// Global single lease for local model inference. Tools, search, and external work may run concurrently;
/// only the local mind step needs this gate.
/// </summary>
public sealed class InferenceLease : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public Task AcquireAsync(CancellationToken cancellationToken = default)
        => _gate.WaitAsync(cancellationToken);

    public bool TryAcquire() => _gate.Wait(0);

    public void Release()
    {
        try { _gate.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    public async Task<T> WithLeaseAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try { return await work(cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
