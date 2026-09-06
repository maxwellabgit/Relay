using Relay.Core.Agents;
using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.External;
using Relay.Core.Ids;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Recovery;
using Relay.Core.Search;
using Relay.Core.SelfChange;
using Relay.Core.Session;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Stream;
using Relay.Core.Workspaces;

namespace Relay.Tests.Support;

/// <summary>One simulated application run against a data root, with real file stores and a fake host and clock.</summary>
public sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The test profile applied to a fresh data root: the judge is off, so the note chord dictates a
    /// note (the path most tests exercise). Listening tests turn the judge on in <c>configure</c>.
    /// Settings already on disk (restarts) are left alone.
    /// </summary>
    public static void TestProfile(RelaySettings s)
    {
        s.Judge.Mode = JudgeSettings.Off;
    }

    public Harness(DataRoot root, Action<RelaySettings>? configure = null, int? failLedgerAfter = null, FixedClock? clock = null,
        IOrchestrator? orchestrator = null, IWorkerHost? workerHost = null, bool inlinePost = true,
        IJudge? judge = null, Func<ExternalModelProfile, IModelClient>? externalClients = null, MemorySecretStore? secrets = null)
    {
        // xUnit installs a SynchronizationContext on the test thread, which stops awaiter continuations from being
        // inlined; the in-process worker pipes depend on inline continuations to keep a whole run on this thread.
        SynchronizationContext.SetSynchronizationContext(null);
        Root = root;
        Clock = clock ?? new FixedClock(T0);
        Scheduler = new ManualScheduler(Clock) { InlinePost = inlinePost };
        Host = new FakeHost();
        Secrets = secrets ?? new MemorySecretStore();

        root.EnsureLayout(Clock);
        var first = SettingsStore.Load(root);
        if (first.CreatedDefault || configure is not null)
        {
            var s = first.Settings;
            if (first.CreatedDefault) TestProfile(s);
            configure?.Invoke(s);
            AtomicFile.WriteAllText(root.SettingsPath, System.Text.Json.JsonSerializer.Serialize(s, RelayJson.Indented));
        }
        SettingsLoad = SettingsStore.Load(root) with { CreatedDefault = first.CreatedDefault };

        Drafts = new FileDraftStore(root);
        Notes = new FileDraftNoteStore(root);
        Sessions = new SessionStore(root);
        Recovery = StartupRecovery.Run(root, Drafts, Sessions, Clock);
        FileLedger = FileLedger.Open(root.LedgerPath, Recovery.Verification, Ulid.NewUlid(Clock.UtcNow), Clock);
        Faulty = new FaultInjectingLedger(FileLedger) { FailAfterAppends = failLedgerAfter ?? int.MaxValue };

        Registry = new ProjectRegistry(root);
        Roots = new WorkspaceRoots(root);
        Excerpts = new ExcerptStore(root);
        ChangeSets = new ChangeSetStore(root);
        Preferences = new PreferenceStore(root, ChangeSets);
        var indexProblems = new List<string>();
        Index = SearchIndex.Build(Recovery.Verification.Records, Notes, Registry, indexProblems, Excerpts);
        if (workerHost is not null) Workers = new WorkerRuntime(root, Registry, workerHost, Clock, Scheduler, SettingsLoad.Settings.Workers);
        if (externalClients is not null)
        {
            External = new ExternalRuntime(root, SettingsLoad.Settings.ExternalModels, externalClients,
                () => new ToolSources { Registry = Registry, Drafts = Notes, Index = Index, Excerpts = Excerpts, ReadArtifact = id => External!.ReadArtifact(id), Preferences = () => Preferences.Compiled() },
                Scheduler, () => Clock.UtcNow);
            foreach (var (id, text, at) in External.AllArtifacts()) Index.IndexArtifact(id, text, at);
        }
        Services = new CoordinatorServices
        {
            Registry = Registry,
            Roots = Roots,
            Orchestrator = orchestrator ?? new RuleBasedOrchestrator(),
            Judge = judge ?? (SettingsLoad.Settings.Judge.Mode == JudgeSettings.Off ? new NullJudge() : new HeuristicJudge()),
            Index = Index,
            Workers = Workers,
            External = External,
            Excerpts = Excerpts,
            ChangeSets = ChangeSets,
            Preferences = Preferences,
            Secrets = Secrets,
            IndexProblems = indexProblems,
        };

        Coordinator = new SessionCoordinator(root, Faulty, Recovery.Verification, Drafts, Notes, Sessions, SettingsLoad, Host, Clock, Scheduler, "0.1.0-test", 4242, Services);
        if (Workers is not null) RelayRuntime.Connect(Workers, Coordinator);
        if (External is not null) External.Completed = Coordinator.CompletePendingOperation;
    }

    public ProjectRegistry Registry { get; }
    public WorkspaceRoots Roots { get; }
    public SearchIndex Index { get; }
    public ExcerptStore Excerpts { get; }
    public ChangeSetStore ChangeSets { get; }
    public PreferenceStore Preferences { get; }
    public ExternalRuntime? External { get; }
    public CoordinatorServices Services { get; }
    public WorkerRuntime? Workers { get; }
    public MemorySecretStore Secrets { get; }

    public DataRoot Root { get; }
    public FixedClock Clock { get; }
    public ManualScheduler Scheduler { get; }
    public FakeHost Host { get; }
    public SettingsStore.LoadResult SettingsLoad { get; }
    public FileDraftStore Drafts { get; }
    public FileDraftNoteStore Notes { get; }
    public SessionStore Sessions { get; }
    public RecoveryReport Recovery { get; }
    public FileLedger FileLedger { get; }
    public FaultInjectingLedger Faulty { get; }
    public SessionCoordinator Coordinator { get; }

    public RelaySnapshot Snap => Coordinator.Snapshot;

    /// <summary>A reader over the same prompt fragments and change sets the coordinator's self-change runtime writes.</summary>
    public SelfChangeRuntime SelfChange => _selfChange ??= new SelfChangeRuntime(Root, Preferences, ChangeSets, () => Clock.UtcNow);
    private SelfChangeRuntime? _selfChange;

    /// <summary>A planner context equivalent to the one the coordinator hands a task, for driving an orchestrator directly.</summary>
    public TurnContext PlannerContext(ITurnSink? sink = null)
    {
        var s = sink ?? new NullTurnSink();
        return new TurnContext
        {
            Tools = new ToolBroker(new ToolSources { Registry = Registry, Drafts = Notes, Index = Index, Excerpts = Excerpts, Preferences = () => Preferences.Compiled() }, s, SettingsLoad.Settings.Orchestrator.MaxToolCalls),
            Sink = s,
            Registry = Registry,
            Roots = Roots,
            Drafts = Notes,
            Settings = SettingsLoad.Settings.Orchestrator,
            Preferences = Preferences.Compiled(),
            ExternalProfiles = External?.ProfileNames ?? [],
            PromptFragment = SelfChange.PromptFragment("planner"),
        };
    }

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
