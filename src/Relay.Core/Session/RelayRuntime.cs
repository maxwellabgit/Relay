using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Recovery;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Session;

/// <summary>
/// Composition root for one application run. Performs the startup sequence in the only safe
/// order: lay out storage → load settings → verify and repair the ledger → open it for append
/// (acquiring the single-writer lock) → build the coordinator → let it record the findings.
/// </summary>
public sealed class RelayRuntime : IDisposable
{
    private RelayRuntime(DataRoot root, FileLedger ledger, SessionCoordinator coordinator, RecoveryReport recovery, SettingsStore.LoadResult settings)
    {
        Root = root;
        Ledger = ledger;
        Coordinator = coordinator;
        Recovery = recovery;
        Settings = settings;
    }

    public DataRoot Root { get; }
    public FileLedger Ledger { get; }
    public SessionCoordinator Coordinator { get; }
    public RecoveryReport Recovery { get; }
    public SettingsStore.LoadResult Settings { get; }

    public static RelayRuntime Create(DataRoot root, ICaptureHost host, Func<RelaySettings, IFlowRelay> relayFactory, IClock clock, IScheduler scheduler, string appVersion, int processId)
    {
        root.EnsureLayout(clock);
        var settings = SettingsStore.Load(root);
        var drafts = new FileDraftStore(root);
        var notes = new FileDraftNoteStore(root);
        var sessions = new SessionStore(root);

        var recovery = StartupRecovery.Run(root, drafts, sessions, clock);
        var sessionId = Ulid.NewUlid(clock.UtcNow);
        var ledger = FileLedger.Open(root.LedgerPath, recovery.Verification, sessionId, clock);

        var coordinator = new SessionCoordinator(
            root, ledger, recovery.Verification, drafts, notes, sessions, settings,
            host, relayFactory(settings.Settings), clock, scheduler, appVersion, processId);

        return new RelayRuntime(root, ledger, coordinator, recovery, settings);
    }

    /// <summary>Records the startup findings and leaves the coordinator in IDLE or LOCKED.</summary>
    public void Start() => Coordinator.Start(Recovery);

    public void Dispose()
    {
        Ledger.Dispose();
    }
}
