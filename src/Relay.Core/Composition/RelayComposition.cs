using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Generation;
using Relay.Core.Ids;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Privacy;
using Relay.Core.Storage;
using Relay.Core.Telemetry;
using Relay.Core.Time;

namespace Relay.Core.Composition;

/// <summary>
/// The only production composition root: one shared store graph, lifecycle-backed decisions,
/// capability registry, runtime, surface, and telemetry.
/// </summary>
public sealed class RelayComposition : IDisposable
{
    private bool _disposed;

    private RelayComposition(
        DataRoot root,
        CaseRuntime runtime,
        CaseRuntimeSurface surface,
        CapabilityRegistry capabilities,
        HostedGrantStore grants,
        JudgmentLifecycle lifecycle,
        ICaseDecisionEngine decisionEngine,
        CaseStore cases,
        ObjectStore objects,
        OperationStore operations,
        ProjectionDatabase projections,
        JudgmentStore judgmentStore,
        JudgmentCache judgmentCache,
        CaseLocalContext local,
        RelaySettings settings,
        RuntimeDiagnostics diagnostics,
        RelayTelemetryPipeline telemetry,
        string runId,
        string runDir,
        string sessionId)
    {
        Root = root;
        Runtime = runtime;
        Surface = surface;
        Capabilities = capabilities;
        Grants = grants;
        Lifecycle = lifecycle;
        DecisionEngine = decisionEngine;
        Cases = cases;
        Objects = objects;
        Operations = operations;
        Projections = projections;
        JudgmentStore = judgmentStore;
        JudgmentCache = judgmentCache;
        Local = local;
        Settings = settings;
        Diagnostics = diagnostics;
        Telemetry = telemetry;
        RunId = runId;
        RunDir = runDir;
        SessionId = sessionId;
    }

    public DataRoot Root { get; }
    public CaseRuntime Runtime { get; }
    public CaseRuntimeSurface Surface { get; }
    public CapabilityRegistry Capabilities { get; }
    public HostedGrantStore Grants { get; }
    public JudgmentLifecycle Lifecycle { get; }
    public ICaseDecisionEngine DecisionEngine { get; }
    public CaseStore Cases { get; }
    public ObjectStore Objects { get; }
    public OperationStore Operations { get; }
    public ProjectionDatabase Projections { get; }
    public JudgmentStore JudgmentStore { get; }
    public JudgmentCache JudgmentCache { get; }
    public CaseLocalContext Local { get; }
    public RelaySettings Settings { get; }
    public RuntimeDiagnostics Diagnostics { get; }
    public RelayTelemetryPipeline Telemetry { get; }
    public string RunId { get; }
    public string RunDir { get; }
    public string SessionId { get; }

    /// <summary>
    /// Builds the production case stack. Callers supply platform clients (Jev, generator);
    /// this method owns shared stores and always wires the decision engine through <see cref="JudgmentLifecycle"/>.
    /// </summary>
    public static RelayComposition Create(
        DataRoot root,
        IClock clock,
        string appVersion,
        IJudgmentClient judgmentClient,
        string? jevModel = null,
        ITextGenerator? generator = null,
        RelaySettings? settings = null,
        Func<ModelHealthView>? modelHealth = null,
        Func<string>? jevStatus = null,
        string? runId = null,
        string? runDir = null,
        string? sessionId = null,
        ResearchServices? research = null,
        ToolServices? tools = null,
        Action? onSideEffect = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(judgmentClient);

        root.EnsureLayout(clock);
        settings ??= SettingsStore.Load(root).Settings;
        var model = string.IsNullOrWhiteSpace(jevModel) ? settings.Jev.Model : jevModel!;

        var cases = new CaseStore(root);
        var objects = new ObjectStore(root, clock);
        var operations = new OperationStore(root);
        var projections = ProjectionDatabase.Open(root);
        var grants = new HostedGrantStore(root, clock);
        var judgmentStore = new JudgmentStore(root, objects, clock);
        var judgmentCache = new JudgmentCache(judgmentStore);
        var disclosure = new DisclosurePolicy(grants, objects, () => clock.UtcNow);
        var lifecycle = new JudgmentLifecycle(
            judgmentStore,
            judgmentCache,
            judgmentClient,
            clock,
            cases: cases,
            disclosure: disclosure,
            grants: grants);

        // Production: lifecycle only — never pass the raw client to the decision engine.
        var engine = new RelayDecisionEngine(lifecycle: lifecycle, model: model);

        var resolvedRunId = !string.IsNullOrWhiteSpace(runId)
            ? runId!
            : Environment.GetEnvironmentVariable("RELAY_RUN_ID") ?? Ulid.NewUlid(clock.UtcNow);

        var resolvedRunDir = !string.IsNullOrWhiteSpace(runDir)
            ? runDir!
            : Environment.GetEnvironmentVariable("RELAY_RUN_DIR");
        if (string.IsNullOrWhiteSpace(resolvedRunDir))
            resolvedRunDir = Path.Combine(root.DevRunsDirectory, resolvedRunId);

        Directory.CreateDirectory(resolvedRunDir);
        var diagnostics = new RuntimeDiagnostics(Path.Combine(resolvedRunDir, "runtime.jsonl"), resolvedRunId);
        var telemetry = new RelayTelemetryPipeline(resolvedRunDir, resolvedRunId, appVersion: appVersion);

        var local = new CaseLocalContext(root, clock);
        var capabilities = BuildCapabilityRegistry(root, local, lifecycle, generator);

        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Ulid.NewUlid(clock.UtcNow)
            : sessionId!;

        var runtime = CaseRuntime.Open(
            root,
            clock,
            engine,
            diagnostics,
            onSideEffect: onSideEffect,
            local: local,
            research: research,
            tools: tools,
            capabilities: capabilities,
            telemetry: telemetry,
            cases: cases,
            objects: objects,
            operations: operations,
            projections: projections,
            sessionId: resolvedSessionId);

        var surface = new CaseRuntimeSurface(
            runtime,
            clock,
            modelHealth: modelHealth,
            grants: grants,
            jevStatus: jevStatus,
            sessionId: resolvedSessionId);

        telemetry.Emit(new ProductEventDraft
        {
            EventName = ProductEventNames.AppStarted,
            Level = ProductEventLevels.Info,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appVersion"] = appVersion,
            },
        });

        return new RelayComposition(
            root,
            runtime,
            surface,
            capabilities,
            grants,
            lifecycle,
            engine,
            cases,
            objects,
            operations,
            projections,
            judgmentStore,
            judgmentCache,
            local,
            settings,
            diagnostics,
            telemetry,
            resolvedRunId,
            resolvedRunDir,
            resolvedSessionId);
    }

    private static CapabilityRegistry BuildCapabilityRegistry(
        DataRoot root,
        CaseLocalContext local,
        JudgmentLifecycle lifecycle,
        ITextGenerator? generator)
    {
        var registry = new CapabilityRegistry();
        var glossary = new GlossaryStore(root);

        // Lifecycle only — no raw IJudgmentClient on production capability handlers.
        registry.Register(
            AcronymResolveCapability.Definition,
            new AcronymResolveCapability(
                glossary,
                id => local.Registry.ById(id)?.RootPath,
                lifecycle: lifecycle));

        registry.Register(
            NoteCaptureCapability.Definition,
            new NoteCaptureCapability(generator: generator, lifecycle: lifecycle));

        registry.Register(TaskCaptureCapability.Definition, new TaskCaptureCapability());

        registry.Register(
            DirectAnswerCapability.Definition,
            new DirectAnswerCapability(
                generator: generator,
                retrieve: q => local.Index.Search(q)
                    .Select(h => new EvidenceHit
                    {
                        SourceRef = $"note:{h.ProjectId}/{h.Id}",
                        Excerpt = h.Excerpt,
                    })
                    .ToList()));

        return registry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Telemetry.Emit(new ProductEventDraft
            {
                EventName = ProductEventNames.AppStopped,
                Level = ProductEventLevels.Info,
            });
        }
        catch { /* best effort */ }
        try { Runtime.SuspendAll(); } catch { /* best effort */ }
        Runtime.Dispose();
        Diagnostics.Dispose();
        Telemetry.Dispose();
    }
}
