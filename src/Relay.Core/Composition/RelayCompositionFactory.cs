using Relay.Core.Cases;
using Relay.Core.Capabilities;
using Relay.Core.Evidence;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Composition;

public enum RelayProviderMode
{
    Fixture,
    Replay,
    Live,
    Production,
}

/// <summary>
/// Shared composition factory for Desktop and DevHarness.
/// Production mode rejects fixture/scripted minds.
/// </summary>
public sealed class RelayCompositionOptions
{
    public required DataRoot Root { get; init; }
    public required IClock Clock { get; init; }
    public required string RunId { get; init; }
    public RelayProviderMode ProviderMode { get; init; } = RelayProviderMode.Fixture;
    public ICaseMind? Mind { get; init; }
    public ResearchServices? Research { get; init; }
    public ToolServices? Tools { get; init; }
    public HostedAuthorization? Hosted { get; init; }
    public Func<ModelHealthView>? ModelHealth { get; init; }
    public Func<ServiceHealthView>? ServiceHealth { get; init; }
    public bool AllowScriptedMinds { get; init; }
}

public sealed class RelayComposition
{
    public required CaseRuntime Runtime { get; init; }
    public required IRelaySurface Surface { get; init; }
    public required RuntimeDiagnostics Diagnostics { get; init; }
    public RetentionService Retention { get; init; } = null!;
    public CapabilityBundleStore Capabilities { get; init; } = null!;
    public CapabilityActivation Activation { get; init; } = null!;
    public RelayProviderMode ProviderMode { get; init; }
}

public static class RelayCompositionFactory
{
    /// <summary>
    /// Builds runtime + surface. Rejects scripted/fixture minds when mode is Production
    /// unless <see cref="RelayCompositionOptions.AllowScriptedMinds"/> is explicitly set
    /// (harness only). SessionCoordinator / TaskLoop / ObservingLoop are not constructed here.
    /// </summary>
    public static RelayComposition Create(RelayCompositionOptions options)
    {
        if (options.ProviderMode == RelayProviderMode.Production && !options.AllowScriptedMinds)
        {
            if (options.Mind is null)
                throw new InvalidOperationException("production_requires_controller_mind");
            if (IsScriptedMind(options.Mind))
                throw new InvalidOperationException("production_rejects_scripted_minds");
        }

        if (options.ProviderMode == RelayProviderMode.Replay)
        {
            // Replay must refuse outbound traffic — leave research/model unbound.
            options = new RelayCompositionOptions
            {
                Root = options.Root,
                Clock = options.Clock,
                RunId = options.RunId,
                ProviderMode = options.ProviderMode,
                Mind = options.Mind,
                Research = new ResearchServices(),
                Tools = options.Tools,
                Hosted = options.Hosted,
                ModelHealth = options.ModelHealth,
                ServiceHealth = options.ServiceHealth,
                AllowScriptedMinds = options.AllowScriptedMinds,
            };
        }

        options.Root.EnsureLayout(options.Clock);
        var diagnosticsPath = Path.Combine(options.Root.DevRunsDirectory, options.RunId, "runtime.jsonl");
        var diagnostics = new RuntimeDiagnostics(diagnosticsPath, options.RunId);

        var mind = options.Mind ?? new ScriptedCaseMind();
        if (options.ProviderMode == RelayProviderMode.Production && !options.AllowScriptedMinds && IsScriptedMind(mind))
            throw new InvalidOperationException("production_rejects_scripted_minds");

        var runtime = CaseRuntime.Open(
            options.Root,
            options.Clock,
            mind,
            diagnostics,
            research: options.Research,
            tools: options.Tools,
            hosted: options.Hosted);

        var evidence = new EvidenceStore(options.Root, options.Clock);
        var retention = new RetentionService(options.Root, evidence, runtime.Objects, options.Clock);
        var caps = new CapabilityBundleStore(options.Root, options.Clock);
        var activation = new CapabilityActivation(caps, options.Clock);

        IRelaySurface surface = new CaseRuntimeSurface(
            runtime,
            options.Clock,
            options.ModelHealth,
            options.ServiceHealth,
            retention,
            caps,
            activation);

        diagnostics.Write(options.Clock.UtcNow, "info", "Composition", "created",
            status: options.ProviderMode.ToString());

        return new RelayComposition
        {
            Runtime = runtime,
            Surface = surface,
            Diagnostics = diagnostics,
            Retention = retention,
            Capabilities = caps,
            Activation = activation,
            ProviderMode = options.ProviderMode,
        };
    }

    private static bool IsScriptedMind(ICaseMind mind)
    {
        var name = mind.Name;
        return name.Contains("scripted", StringComparison.OrdinalIgnoreCase)
               || mind is ScriptedCaseMind
               || mind is ListeningScriptedMind
               || mind is SimpleDirectMind
               || mind is OriginRoutingMind;
    }
}
