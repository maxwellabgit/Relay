using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Generation;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Model;
using Relay.Core.Privacy;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Gateway;

namespace Relay.Desktop;

/// <summary>
/// Production composition for Conversation-to-Action: CaseRuntime + decision engine + surface.
/// Does not construct SessionCoordinator / RelayRuntime.
/// </summary>
public sealed class CaseRelayHost : IDisposable
{
    private readonly TypeSafeJudgmentClient? _judgmentClient;
    private readonly RuntimeDiagnostics _diagnostics;
    private bool _disposed;

    private CaseRelayHost(
        DataRoot root,
        CaseRuntime runtime,
        CaseRuntimeSurface surface,
        CapabilityRegistry capabilities,
        HostedGrantStore grants,
        ISecretStore secrets,
        RelaySettings settings,
        TypeSafeJudgmentClient? judgmentClient,
        RuntimeDiagnostics diagnostics)
    {
        Root = root;
        Runtime = runtime;
        Surface = surface;
        Capabilities = capabilities;
        Grants = grants;
        Secrets = secrets;
        Settings = settings;
        _judgmentClient = judgmentClient;
        _diagnostics = diagnostics;
    }

    public DataRoot Root { get; }
    public CaseRuntime Runtime { get; }
    public IRelaySurface Surface { get; }
    public CapabilityRegistry Capabilities { get; }
    public HostedGrantStore Grants { get; }
    public ISecretStore Secrets { get; }
    public RelaySettings Settings { get; }

    public static CaseRelayHost Create(DataRoot root, IClock clock, string version)
    {
        root.EnsureLayout(clock);
        var settings = SettingsStore.Load(root).Settings;
        var secrets = ModelComposition.Secrets(root);
        var grants = new HostedGrantStore(root, clock);

        TypeSafeJudgmentClient? judgment = null;
        try
        {
            if (settings.Jev.Enabled)
                judgment = new TypeSafeJudgmentClient(settings.Jev, secrets);
        }
        catch (ArgumentException)
        {
            judgment = null;
        }

        IJudgmentClient judgmentClient = (IJudgmentClient?)judgment ?? new NullJudgmentClient();
        var engine = new RelayDecisionEngine(client: judgmentClient, model: settings.Jev.Model);
        var mind = new CaseMindDecisionAdapter(engine);

        var runDir = Path.Combine(root.DevRunsDirectory, "desktop", version.Replace('/', '_'));
        Directory.CreateDirectory(runDir);
        var diagnostics = new RuntimeDiagnostics(Path.Combine(runDir, "runtime.jsonl"), "desktop");

        var local = new CaseLocalContext(root, clock);
        var runtime = CaseRuntime.Open(root, clock, mind, diagnostics, local: local);
        var surface = new CaseRuntimeSurface(
            runtime,
            clock,
            modelHealth: () => ModelHealthView.Placeholder,
            grants: grants,
            jevStatus: () => judgment is null
                ? HostedJudgmentView.Unavailable
                : settings.Jev.Enabled
                    ? HostedJudgmentView.Ready
                    : HostedJudgmentView.Disabled,
            sessionId: UlidSession(clock));

        var capabilities = new CapabilityRegistry();
        var glossary = new GlossaryStore(root);
        capabilities.Register(AcronymResolveCapability.Definition,
            new AcronymResolveCapability(glossary, id => local.Registry.ById(id)?.RootPath, client: judgmentClient));
        capabilities.Register(NoteCaptureCapability.Definition, new NoteCaptureCapability(client: judgmentClient));
        capabilities.Register(TaskCaptureCapability.Definition, new TaskCaptureCapability());
        capabilities.Register(DirectAnswerCapability.Definition, new DirectAnswerCapability());

        return new CaseRelayHost(root, runtime, surface, capabilities, grants, secrets, settings, judgment, diagnostics);
    }

    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < 8; i++)
        {
            var stepped = await Runtime.StepNextAsync(cancellationToken).ConfigureAwait(false);
            if (stepped is null) break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Runtime.SuspendAll(); } catch { /* best effort */ }
        Runtime.Dispose();
        _judgmentClient?.Dispose();
        _diagnostics.Dispose();
    }

    private static string UlidSession(IClock clock) =>
        Relay.Core.Ids.Ulid.NewUlid(clock.UtcNow);
}

/// <summary>Absent TypeSafe binding — engine waits rather than inventing judgments.</summary>
file sealed class NullJudgmentClient : IJudgmentClient
{
    public string ProviderName => "null";

    public Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken)
        => Task.FromResult(JudgmentResponse.FromFailure(
            JudgmentFailure.Create(JudgmentFailureCategories.Disabled, "No TypeSafe client bound.")));
}
