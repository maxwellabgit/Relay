using Relay.Core.Agents;
using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Execution;
using Relay.Core.External;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Notes;
using Relay.Core.Usage;
using Relay.Core.Orchestration;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Recovery;
using Relay.Core.Search;
using Relay.Core.SelfChange;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Stream;
using Relay.Core.Time;
using Relay.Core.Tools;
using Relay.Core.Workspaces;
using Relay.Core.Workflows;

namespace Relay.Core.Session;

/// <summary>Host-supplied factories for the parts that depend on platform or network.</summary>
public sealed class RuntimeOptions
{
    /// <summary>Builds a chat client for a model endpoint (RELAY0's own, or an external profile). Null keeps everything local and heuristic.</summary>
    public Func<ModelSettings, IModelClient?>? ModelClientFactory { get; init; }
    /// <summary>Builds the online search client when search is enabled; null keeps web_search unavailable.</summary>
    public Func<SearchSettings, ISearchClient?>? SearchClientFactory { get; init; }
    /// <summary>Builds the process host for workers (job object on Windows); null or a null result disables workers.</summary>
    public Func<RelaySettings, IWorkerHost?>? WorkerHostFactory { get; init; }
    /// <summary>The protected secret store for API keys; null when the host has none.</summary>
    public ISecretStore? Secrets { get; init; }
}

/// <summary>
/// Composition root for one application run. Performs the startup sequence in the only safe
/// order: lay out storage → load settings → verify and repair the ledger → open it for append
/// (acquiring the single-writer lock) → build the projections, mind, planner, runtimes and the
/// coordinator → let it record what startup found.
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
        var excerpts = new ExcerptStore(root);
        var changeSets = new ChangeSetStore(root);
        var preferences = new PreferenceStore(root, changeSets);
        var indexProblems = new List<string>();
        var index = SearchIndex.Build(recovery.Verification.Records, notes, registry, indexProblems, excerpts);

        WorkerRuntime? workers = null;
        ToolRuntime? tools = null;
        var current = settings.Settings; // the settings the tool drafter reads; replaced when the UI changes them
        if (settings.Settings.Workers.Enabled && options.WorkerHostFactory?.Invoke(settings.Settings) is { } workerHost)
        {
            workers = new WorkerRuntime(root, registry, workerHost, clock, scheduler, settings.Settings.Workers);
            // Built tools share the worker sandbox; the drafter is RELAY0's model, so building is only possible when the model is on.
            tools = new ToolRuntime(root, changeSets, workerHost, settings.Settings.Workers,
                () => current.Model.Enabled ? options.ModelClientFactory?.Invoke(current.Model) : null, () => clock.UtcNow);
        }
        var workflows = new WorkflowRuntime(root, changeSets, () => clock.UtcNow);

        ExternalRuntime? external = null;
        SearchArtifacts? searchArtifacts = null;
        ISearchClient? searchClient = null;
        CoordinatorServices? servicesRef = null;
        if (settings.Settings.Search.Enabled && options.SearchClientFactory is { } searchFactory)
        {
            searchClient = searchFactory(settings.Settings.Search);
            if (searchClient is not null)
            {
                searchArtifacts = new SearchArtifacts(root, () => clock.UtcNow);
                foreach (var (id, text, at) in searchArtifacts.All()) index.IndexArtifact(id, text, at);
            }
        }
        if (options.ModelClientFactory is { } factory)
        {
            external = new ExternalRuntime(root, settings.Settings.ExternalModels, profile => factory(profile.AsModelSettings()) ?? throw new ArgumentException("no client for profile " + profile.Name),
                () => new ToolSources
                {
                    Registry = registry,
                    Drafts = notes,
                    Index = servicesRef!.Index,
                    Excerpts = excerpts,
                    ReadArtifact = id => servicesRef!.SearchArtifacts?.Read(id) ?? servicesRef!.External!.ReadArtifact(id),
                    Preferences = () => preferences.Compiled(),
                    Search = servicesRef!.Search,
                    SearchArtifacts = servicesRef!.SearchArtifacts,
                    OnlineSearchGranted = () => preferences.Compiled().AllowOnlineSearch,
                },
                scheduler, () => clock.UtcNow)
            {
                // Replies are digested into feed lines by RELAY0 (digest.md); with the local model off, the digest is the reply's own first lines.
                Digester = () => current.Model.Enabled ? factory(current.Model) : null,
                DigestInstructions = () => AtomicFile.ReadAllTextIfExists(Path.Combine(root.PromptsDirectory, Digest.PromptName + ".md")),
                SearchAvailable = searchClient is not null,
            };
            foreach (var (id, text, at) in external.AllArtifacts()) index.IndexArtifact(id, text, at);
        }

        var decisions = DecisionSet.Load(root, out var decisionProblems);
        indexProblems.AddRange(decisionProblems);
        var services = servicesRef = new CoordinatorServices
        {
            Registry = registry,
            Roots = roots,
            Mind = BuildMind(settings.Settings, options),
            Decisions = decisions,
            Usage = new UsageRecorder(root),
            Index = index,
            Workers = workers,
            Tools = tools,
            Workflows = workflows,
            External = external,
            Search = searchClient,
            SearchArtifacts = searchArtifacts,
            Excerpts = excerpts,
            ChangeSets = changeSets,
            Preferences = preferences,
            Secrets = options.Secrets,
            IndexProblems = indexProblems,
        };
        if (tools is not null) indexProblems.AddRange(tools.Store.Problems());
        indexProblems.AddRange(workflows.Store.Problems());

        var coordinator = new SessionCoordinator(
            root, ledger, recovery.Verification, drafts, notes, sessions, settings,
            host, clock, scheduler, appVersion, processId, services);
        if (workers is not null) Connect(workers, coordinator);
        if (external is not null)
        {
            external.Completed = coordinator.CompletePendingOperation;
            external.Progress = coordinator.ReportDelegateProgress;
        }
        // Model and mind settings changed in the UI take effect on the next task or listening pass.
        coordinator.SettingsChanged += changed =>
        {
            current = changed;
            services.Mind = BuildMind(changed, options);
        };

        return new RelayRuntime(root, ledger, coordinator, recovery, settings, services);
    }

    /// <summary>The one interpreter (docs/09): RELAY0's model behind the step schema. Null unless mind mode is selected and the model enabled.</summary>
    public static IMind? BuildMind(RelaySettings settings, RuntimeOptions options)
    {
        if (settings.Orchestrator.Mode != OrchestratorSettings.Mind || !settings.Model.Enabled) return null;
        return options.ModelClientFactory?.Invoke(settings.Model) is { } client ? new ModelMind(client, settings.Model.MaxOutputTokens) : null;
    }

    /// <summary>Worker results re-enter the coordinator as pending-operation completions; stop requests flow the other way.</summary>
    public static void Connect(WorkerRuntime workers, SessionCoordinator coordinator)
    {
        workers.Completed = coordinator.CompletePendingOperation;
        coordinator.RequestWorkerStop = proposalId => workers.Stop(proposalId, "stop requested");
    }

    /// <summary>Records the startup findings and leaves the coordinator in IDLE or LOCKED.</summary>
    public void Start() => Coordinator.Start(Recovery);

    public void Dispose()
    {
        Ledger.Dispose();
    }
}
