namespace Relay.Core.Tasks;

/// <summary>
/// The runtime scheduler over <c>TaskLoop</c>: a ready queue that starts mind-loop work in due order,
/// and a single local-inference gate so exactly one <c>StepAsync</c> or digest runs at a time while
/// tools, workers, external HTTP and approvals may proceed concurrently.
/// </summary>
public sealed class TaskEngine : IDisposable
{
    private readonly SemaphoreSlim _inference = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<ReadyWork> _ready = new();
    private readonly Action<Action> _post;
    private bool _disposed;

    private sealed record ReadyWork(string TaskId, Func<Task> Run, Action<Task> OnReturned, DateTimeOffset ReadyAt);

    public TaskEngine(Action<Action> post) => _post = post ?? throw new ArgumentNullException(nameof(post));

    /// <summary>Acquires the local-inference gate. Hold only across <c>IMind.StepAsync</c> or a digest — never across tools, workers or external HTTP.</summary>
    public Task AcquireInferenceAsync(CancellationToken cancellationToken) => _inference.WaitAsync(cancellationToken);

    public void ReleaseInference()
    {
        try { _inference.Release(); }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (SemaphoreFullException) { /* double-release after cancel; ignore */ }
    }

    /// <summary>
    /// Queues mind-loop work for <paramref name="taskId"/> and pumps the ready queue. Multiple tasks may be
    /// mid-loop (tools / waits in flight); only inference is serialized via <see cref="AcquireInferenceAsync"/>.
    /// </summary>
    public void Enqueue(string taskId, Func<Task> run, Action<Task> onReturned, DateTimeOffset readyAt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _ready.Enqueue(new ReadyWork(taskId, run, onReturned, readyAt == default ? DateTimeOffset.UtcNow : readyAt));
        }
        Pump();
    }

    /// <summary>How many ready items are waiting to start (not including work already in flight).</summary>
    public int ReadyCount { get { lock (_sync) return _ready.Count; } }

    private void Pump()
    {
        while (true)
        {
            ReadyWork next;
            lock (_sync)
            {
                if (_ready.Count == 0) return;
                // Due order: items are enqueued when they become ready; equal times stay FIFO.
                next = _ready.Dequeue();
            }

            Task running;
            try { running = next.Run(); }
            catch (Exception ex) { running = Task.FromException(ex); }

            var work = next;
            running.ContinueWith(
                t => _post(() => work.OnReturned(t)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync) _ready.Clear();
        _inference.Dispose();
    }
}
