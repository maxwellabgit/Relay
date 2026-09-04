using Relay.Core.Agents;
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
using Relay.Core.Session;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Workspaces;

namespace Relay.Tests.Support;

/// <summary>One simulated application run against a data root, with real file stores and fake host/relay/time.</summary>
public sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public Harness(DataRoot root, bool relayEnabled = false, Action<RelaySettings>? configure = null, int? failLedgerAfter = null, FixedClock? clock = null,
        IOrchestrator? orchestrator = null, IWorkerHost? workerHost = null, bool inlinePost = true)
    {
        // xUnit installs a SynchronizationContext on the test thread, which stops awaiter continuations from being
        // inlined; the in-process worker pipes depend on inline continuations to keep a whole run on this thread.
        SynchronizationContext.SetSynchronizationContext(null);
        Root = root;
        Clock = clock ?? new FixedClock(T0);
        Scheduler = new ManualScheduler(Clock) { InlinePost = inlinePost };
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

        Registry = new ProjectRegistry(root);
        Roots = new WorkspaceRoots(root);
        var indexProblems = new List<string>();
        Index = SearchIndex.Build(Recovery.Verification.Records, Notes, Registry, indexProblems);
        if (workerHost is not null) Workers = new WorkerRuntime(root, Registry, workerHost, Clock, Scheduler, SettingsLoad.Settings.Workers);
        Services = new CoordinatorServices
        {
            Registry = Registry,
            Roots = Roots,
            Orchestrator = orchestrator ?? new RuleBasedOrchestrator(),
            Index = Index,
            Workers = Workers,
            IndexProblems = indexProblems,
        };

        Coordinator = new SessionCoordinator(root, Faulty, Recovery.Verification, Drafts, Notes, Sessions, SettingsLoad, Host, Relay, Clock, Scheduler, "0.1.0-test", 4242, Services);
        if (Workers is not null) RelayRuntime.Connect(Workers, Coordinator);
    }

    public ProjectRegistry Registry { get; }
    public WorkspaceRoots Roots { get; }
    public SearchIndex Index { get; }
    public CoordinatorServices Services { get; }
    public WorkerRuntime? Workers { get; }

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
        try { Workers?.Stop(null, "test disposed"); } catch { }
        try { FileLedger.Dispose(); } catch { }
    }
}
