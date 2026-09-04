using Relay.Core.Input;
using Relay.Core.Ledger;
using Relay.Core.Session;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Tests.Support;

public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Deterministic scheduler: callbacks run only when the test advances virtual time.</summary>
public sealed class ManualScheduler : IScheduler
{
    private readonly FixedClock _clock;
    private readonly List<Entry> _entries = new();

    public ManualScheduler(FixedClock clock) => _clock = clock;

    public int Pending => _entries.Count(e => !e.Cancelled);

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new Entry(_clock.UtcNow + delay, callback);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Posted work runs immediately: tests control asynchrony at the orchestrator/worker fakes instead.</summary>
    public void Post(Action action) => action();

    public void Advance(TimeSpan by)
    {
        var target = _clock.UtcNow + by;
        while (true)
        {
            var next = _entries.Where(e => !e.Cancelled && e.Due <= target).OrderBy(e => e.Due).FirstOrDefault();
            if (next is null) break;
            _entries.Remove(next);
            _clock.UtcNow = next.Due;
            next.Callback();
        }
        _clock.UtcNow = target;
        _entries.RemoveAll(e => e.Cancelled);
    }

    private sealed class Entry : IDisposable
    {
        public Entry(DateTimeOffset due, Action callback) { Due = due; Callback = callback; }
        public DateTimeOffset Due { get; }
        public Action Callback { get; }
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }
}

public sealed class FakeHost : ICaptureHost
{
    public int PrepareCalls { get; private set; }
    public bool Foreground { get; set; } = true;
    public string? ForegroundProcess { get; set; } = "notepad";

    public void PrepareCaptureSurface() => PrepareCalls++;
    public bool IsCaptureSurfaceForeground() => Foreground;
    public string? ForegroundProcessName() => ForegroundProcess;
}

public sealed class FakeRelay : IFlowRelay
{
    public FakeRelay(bool enabled, string chord = "Ctrl+Win+F24")
    {
        Enabled = enabled;
        Chord = KeyChord.Parse(chord);
    }

    public bool Enabled { get; }
    public KeyChord? Chord { get; }
    public List<RelayPurpose> Sent { get; } = new();
    public bool Fail { get; set; }

    public RelayResult SendHandsFreeToggle(RelayPurpose purpose)
    {
        if (Fail) return new RelayResult(false, "SendInput returned 0");
        Sent.Add(purpose);
        return new RelayResult(true, null);
    }
}

/// <summary>Wraps a real ledger and starts failing after a configurable number of appends.</summary>
public sealed class FaultInjectingLedger : ILedger
{
    private readonly ILedger _inner;

    public FaultInjectingLedger(ILedger inner) => _inner = inner;

    public int FailAfterAppends { get; set; } = int.MaxValue;
    public int Appends { get; private set; }

    public string Path => _inner.Path;
    public long LastSeq => _inner.LastSeq;
    public string LastHash => _inner.LastHash;
    public string SessionId => _inner.SessionId;

    public event Action<LedgerRecord>? Appended
    {
        add => _inner.Appended += value;
        remove => _inner.Appended -= value;
    }

    public LedgerRecord Append(string type, object data)
    {
        Appends++;
        if (Appends > FailAfterAppends) throw new LedgerWriteException("simulated disk failure", new IOException("disk full"));
        return _inner.Append(type, data);
    }
}

public sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Root = new DataRoot(path);
    }

    public DataRoot Root { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root.Path, recursive: true); } catch { /* best effort */ }
    }
}
