using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Recovery;
using Relay.Core.Session;
using Relay.Core.Sessions;
using Relay.Core.Storage;

namespace Relay.Tests.Support;

/// <summary>One simulated application run against a data root, with real file stores and fake host/relay/time.</summary>
public sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public Harness(DataRoot root, bool relayEnabled = false, Action<RelaySettings>? configure = null, int? failLedgerAfter = null, FixedClock? clock = null)
    {
        Root = root;
        Clock = clock ?? new FixedClock(T0);
        Scheduler = new ManualScheduler(Clock);
        Host = new FakeHost();
        Relay = new FakeRelay(relayEnabled);

        root.EnsureLayout(Clock);
        if (configure is not null)
        {
            var s = SettingsStore.Load(root).Settings;
            configure(s);
            AtomicFile.WriteAllText(root.SettingsPath, System.Text.Json.JsonSerializer.Serialize(s, RelayJson.Indented));
        }
        SettingsLoad = SettingsStore.Load(root);

        Drafts = new FileDraftStore(root);
        Notes = new FileDraftNoteStore(root);
        Sessions = new SessionStore(root);
        Recovery = StartupRecovery.Run(root, Drafts, Sessions, Clock);
        FileLedger = FileLedger.Open(root.LedgerPath, Recovery.Verification, Ulid.NewUlid(Clock.UtcNow), Clock);
        Faulty = new FaultInjectingLedger(FileLedger) { FailAfterAppends = failLedgerAfter ?? int.MaxValue };

        Coordinator = new SessionCoordinator(root, Faulty, Recovery.Verification, Drafts, Notes, Sessions, SettingsLoad, Host, Relay, Clock, Scheduler, "0.1.0-test", 4242);
    }

    public DataRoot Root { get; }
    public FixedClock Clock { get; }
    public ManualScheduler Scheduler { get; }
    public FakeHost Host { get; }
    public FakeRelay Relay { get; }
    public SettingsStore.LoadResult SettingsLoad { get; }
    public FileDraftStore Drafts { get; }
    public FileDraftNoteStore Notes { get; }
    public SessionStore Sessions { get; }
    public RecoveryReport Recovery { get; }
    public FileLedger FileLedger { get; }
    public FaultInjectingLedger Faulty { get; }
    public SessionCoordinator Coordinator { get; }

    public RelaySnapshot Snap => Coordinator.Snapshot;

    public Harness Start()
    {
        Coordinator.Start(Recovery);
        return this;
    }

    /// <summary>Simulates a crash: the ledger handle is released without a clean shutdown.</summary>
    public void Crash() => FileLedger.Dispose();

    public void CleanExit(string reason = "user_exit")
    {
        Coordinator.Shutdown(reason);
        FileLedger.Dispose();
    }

    /// <summary>Every decodable record, regardless of chain health (tests inspect records written after a deliberate break).</summary>
    public IReadOnlyList<LedgerRecord> Records()
    {
        var result = new List<LedgerRecord>();
        foreach (var line in LedgerText().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = LedgerFormat.TryDecode(line.TrimEnd('\r'), out _);
            if (record is not null) result.Add(record);
        }
        return result;
    }

    public string LedgerText()
    {
        using var stream = new FileStream(Root.LedgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public LedgerRecord? Last(string type) => Records().LastOrDefault(r => r.Type == type);

    public int Count(string type) => Records().Count(r => r.Type == type);

    public void Dispose()
    {
        try { FileLedger.Dispose(); } catch { }
    }
}
