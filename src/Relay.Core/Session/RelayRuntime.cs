using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Projects;
using Relay.Core.Recovery;
using Relay.Core.Search;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Core.Workspaces;

namespace Relay.Core.Session;

/// <summary>Host-supplied factories for the parts that depend on platform or network.</summary>
public sealed class RuntimeOptions
{
    public required Func<RelaySettings, IFlowRelay> RelayFactory { get; init; }
    /// <summary>Builds the model-backed orchestrator when settings enable it; null keeps rules only.</summary>
    public Func<RelaySettings, IOrchestrator?>? ModelOrchestratorFactory { get; init; }
    public Func<RelaySettings, DataRoot, IWorkerOperations?>? WorkerFactory { get; init; }
}

/// <summary>
/// Composition root for one application run. Performs the startup sequence in the only safe
/// order: lay out storage → load settings → verify and repair the ledger → open it for append
/// (acquiring the single-writer lock) → build the projections and coordinator → let it record the findings.
/// </summary>
public sealed class RelayRuntime : IDisposable
{
    private RelayRuntime(DataRoot root, FileLedger ledger, SessionCoordinator coordinator, RecoveryReport recovery, SettingsStore.LoadResult settings, CoordinatorServices services)
    {
        Root = root;
        Ledger = ledger;
        Coordinator = coordinator;
        Recovery = recovery;
        Settings = settings;
        Services = services;
    }

    public DataRoot Root { get; }
    public FileLedger Ledger { get; }
    public SessionCoordinator Coordinator { get; }
    public RecoveryReport Recovery { get; }
    public SettingsStore.LoadResult Settings { get; }
    public CoordinatorServices Services { get; }

    public static RelayRuntime Create(DataRoot root, ICaptureHost host, Func<RelaySettings, IFlowRelay> relayFactory, IClock clock, IScheduler scheduler, string appVersion, int processId)
        => Create(root, host, new RuntimeOptions { RelayFactory = relayFactory }, clock, scheduler, appVersion, processId);

    public static RelayRuntime Create(DataRoot root, ICaptureHost host, RuntimeOptions options, IClock clock, IScheduler scheduler, string appVersion, int processId)
    {
        root.EnsureLayout(clock);
        var settings = SettingsStore.Load(root);
        var drafts = new FileDraftStore(root);
        var notes = new FileDraftNoteStore(root);
        var sessions = new SessionStore(root);

        var recovery = StartupRecovery.Run(root, drafts, sessions, clock);
        var sessionId = Ulid.NewUlid(clock.UtcNow);
        var ledger = FileLedger.Open(root.LedgerPath, recovery.Verification, sessionId, clock);

        var registry = new ProjectRegistry(root);
        var roots = new WorkspaceRoots(root);
        var indexProblems = new List<string>();
        var index = SearchIndex.Build(recovery.Verification.Records, notes, registry, indexProblems);

        IOrchestrator orchestrator = new RuleBasedOrchestrator();
        if (settings.Settings.Orchestrator.Mode == OrchestratorSettings.RulesAndModel && settings.Settings.Model.Enabled && options.ModelOrchestratorFactory?.Invoke(settings.Settings) is { } model)
        {
            orchestrator = new CompositeOrchestrator(orchestrator, model);
        }

        var services = new CoordinatorServices
        {
            Registry = registry,
            Roots = roots,
            Orchestrator = orchestrator,
            Index = index,
            Workers = options.WorkerFactory?.Invoke(settings.Settings, root),
            IndexProblems = indexProblems,
        };

        var coordinator = new SessionCoordinator(
            root, ledger, recovery.Verification, drafts, notes, sessions, settings,
            host, options.RelayFactory(settings.Settings), clock, scheduler, appVersion, processId, services);

        return new RelayRuntime(root, ledger, coordinator, recovery, settings, services);
    }

    /// <summary>Records the startup findings and leaves the coordinator in IDLE or LOCKED.</summary>
    public void Start() => Coordinator.Start(Recovery);

    public void Dispose()
    {
        Ledger.Dispose();
    }
}
