using Microsoft.UI.Dispatching;
using Relay.Core.Time;

namespace Relay.Desktop;

/// <summary>One-shot timers on the UI thread's dispatcher queue, so every coordinator callback runs where the coordinator lives.</summary>
public sealed class DispatcherScheduler : IScheduler
{
    private readonly DispatcherQueue _queue;

    public DispatcherScheduler(DispatcherQueue queue)
    {
        _queue = queue;
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay;
        timer.IsRepeating = false;
        var handle = new Handle(timer);
        timer.Tick += (_, _) =>
        {
            if (handle.Cancelled) return;
            handle.Cancelled = true;
            callback();
        };
        timer.Start();
        return handle;
    }

    public void Post(Action action)
    {
        if (!_queue.TryEnqueue(() => action()))
        {
            // The queue is shutting down; there is no coordinator thread left to run on.
        }
    }

    private sealed class Handle : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        public Handle(DispatcherQueueTimer timer) => _timer = timer;
        public bool Cancelled { get; set; }
        public void Dispose()
        {
            Cancelled = true;
            _timer.Stop();
        }
    }
}
