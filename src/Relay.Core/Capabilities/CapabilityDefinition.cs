namespace Relay.Core.Capabilities;

/// <summary>Stable capability contract — code owns transitions; providers never invent new capabilities.</summary>
public sealed class CapabilityDefinition
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<string> AllowedOrigins { get; init; }
    public required string SideEffectClass { get; init; }
    public IReadOnlyList<string> EvaluationFixtures { get; init; } = [];
    public string? HandlerKey { get; init; }

    public string AtVersion => $"{Id}@{Version}";
}

public static class CapabilitySideEffects
{
    public const string None = "none";
    public const string LocalWrite = "local-write";
    public const string CanonicalWrite = "canonical-write";
    public const string Hosted = "hosted";
    public const string External = "external-side-effecting";
}

public sealed class CapabilityRequest
{
    public required string CapabilityId { get; init; }
    public required int CapabilityVersion { get; init; }
    public required string CaseId { get; init; }
    public required string Origin { get; init; }
    public string? ProjectId { get; init; }
    public string? Objective { get; init; }
    public IReadOnlyDictionary<string, string> Arguments { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> SourceRefs { get; init; } = [];
    public DateTimeOffset At { get; init; }
}

public sealed class CapabilityResult
{
    public required string Kind { get; init; }
    public string FeedText { get; init; } = "";
    public string? PresentationLevel { get; init; }
    public bool Done { get; init; }
    public string? Reason { get; init; }
    public Dictionary<string, string> Artifacts { get; init; } = new(StringComparer.Ordinal);
    public List<string> JudgmentIds { get; init; } = [];
    public List<string> SourceRefs { get; init; } = [];
}

public interface ICapabilityHandler
{
    string CapabilityId { get; }
    int Version { get; }
    Task<CapabilityResult> HandleAsync(CapabilityRequest request, CancellationToken cancellationToken);
}

/// <summary>Registers the four v0.1 capabilities as they land.</summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityDefinition> _defs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICapabilityHandler> _handlers = new(StringComparer.Ordinal);

    public void Register(CapabilityDefinition definition, ICapabilityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        if (!string.Equals(definition.Id, handler.CapabilityId, StringComparison.Ordinal) ||
            definition.Version != handler.Version)
            throw new InvalidOperationException("Handler id/version must match definition.");

        _defs[definition.AtVersion] = definition;
        _handlers[definition.AtVersion] = handler;
    }

    public bool TryGet(string idAtVersion, out CapabilityDefinition? definition, out ICapabilityHandler? handler)
    {
        definition = null;
        handler = null;
        if (!_defs.TryGetValue(idAtVersion, out var def)) return false;
        if (!_handlers.TryGetValue(idAtVersion, out var h)) return false;
        definition = def;
        handler = h;
        return true;
    }

    public IReadOnlyCollection<string> Enabled => _defs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
}
