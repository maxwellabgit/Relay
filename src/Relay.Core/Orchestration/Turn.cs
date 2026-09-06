using Relay.Core.Config;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Tasks;
using Relay.Core.Workspaces;

namespace Relay.Core.Orchestration;

/// <summary>
/// What a planner is asked to work on: for a direct ask the instruction verbatim; for an observed
/// task the judge's focused prompt, which quotes the words that triggered it. <see cref="Origin"/>
/// and <see cref="Kind"/> tell the planner which lane it is in and therefore what it may propose.
/// </summary>
public sealed record TurnRequest(
    string TurnId,
    string CaptureId,
    string SourceEventId,
    string Instruction,
    DateTimeOffset At,
    TaskOrigin Origin = TaskOrigin.Direct,
    TaskKind Kind = TaskKind.Answer,
    string? ExcerptId = null,
    /// <summary>For a follow-up about an external result: the stored artifact to summarise.</summary>
    string? ArtifactId = null);

/// <summary>A pointer from an answer back to the words that support it.</summary>
public sealed record Citation(string Kind, string Id, string? ProjectId, string? ProjectSlug, string Excerpt, SourceSpan? Span);

/// <summary>
/// What a planner returns for one task: a visible plan, an optional answer with citations, the
/// knowledge state it reached, and zero or more proposals. The plan never executes anything; the
/// coordinator decides, asks, and runs. <see cref="Understood"/> false means the producer could not
/// interpret the request, which lets a fallback take over.
/// </summary>
public sealed record TurnPlan(
    bool Understood,
    string Summary,
    IReadOnlyList<string> Steps,
    string? Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<Proposal> Proposals,
    string Producer,
    string? Raw = null,
    KnowledgeState? Knowledge = null,
    /// <summary>For check tasks: true when the stated fact agrees with what is stored, false when it conflicts, null when the planner could not tell.</summary>
    bool? Consistent = null)
{
    public static TurnPlan NotUnderstood(string producer, string summary) => new(false, summary, [], null, [], [], producer);
}

/// <summary>Live progress from inside a task. Implementations must be safe to call from any thread.</summary>
public interface ITurnSink
{
    void Progress(string text);
    void ToolCalled(string tool, IReadOnlyDictionary<string, string> args);
    void ToolReturned(string tool, bool ok, string summary, int items);
    void ModelRequested(string host, string model, int promptChars, int sources);
    void ModelResponded(bool ok, int chars, long elapsedMs, string? error, int promptTokens = 0, int completionTokens = 0);
}

public sealed class TurnContext
{
    public required ToolBroker Tools { get; init; }
    public required ITurnSink Sink { get; init; }
    public required ProjectRegistry Registry { get; init; }
    public required WorkspaceRoots Roots { get; init; }
    public required IDraftNoteStore Drafts { get; init; }
    public required OrchestratorSettings Settings { get; init; }
    /// <summary>Completed, not-yet-applied worker runs for a project (newest first); empty when workers are not configured.</summary>
    public Func<string, IReadOnlyList<Agents.AgentRunStatus>> CompletedRuns { get; init; } = _ => [];
    /// <summary>The user's compiled preferences: prompt fragment, answer limits, watched terms, grants.</summary>
    public CompiledPreferences? Preferences { get; init; }
    /// <summary>Names of configured external model profiles the planner may propose sending a package to.</summary>
    public IReadOnlyList<string> ExternalProfiles { get; init; } = [];
    /// <summary>The subset of <see cref="ExternalProfiles"/> whose host can search online when a request is approved with allowSearch.</summary>
    public IReadOnlyList<string> SearchProfiles { get; init; } = [];
    /// <summary>Extra prompt text approved through change sets (the 'planner' fragment).</summary>
    public string? PromptFragment { get; init; }
}

public interface IOrchestrator
{
    string Name { get; }
    Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Two planners in sequence: the primary answers when it understands the request; otherwise the
/// fallback is asked. Relay runs the deterministic grammar first (exact and free for the fixed
/// commands) and RELAY0's model second, for everything phrased in prose.
/// </summary>
public sealed class CompositeOrchestrator : IOrchestrator
{
    private readonly IOrchestrator _primary;
    private readonly IOrchestrator? _fallback;

    public CompositeOrchestrator(IOrchestrator primary, IOrchestrator? fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => _fallback is null ? _primary.Name : $"{_primary.Name}+{_fallback.Name}";

    public async Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        var plan = await _primary.PlanAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (plan.Understood || _fallback is null) return plan;
        context.Sink.Progress($"{_primary.Name} could not handle the request; falling back to {_fallback.Name}");
        var fallback = await _fallback.PlanAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (fallback.Understood) return fallback;
        // Neither understood. The fallback was the last, fuller attempt and its summary says what went wrong
        // (model unavailable, contract broken, budget spent); the primary's verdict is kept as a step.
        return fallback with { Steps = [$"{_primary.Name}: {plan.Summary}", .. fallback.Steps], Raw = fallback.Raw ?? plan.Raw };
    }
}
