using Relay.Core.Config;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Workspaces;

namespace Relay.Core.Orchestration;

/// <summary>One command-mode instruction, exactly as stored, with the ledger record that holds it.</summary>
public sealed record TurnRequest(string TurnId, string CaptureId, string SourceEventId, string Instruction, DateTimeOffset At);

/// <summary>A pointer from an answer back to the words that support it.</summary>
public sealed record Citation(string Kind, string Id, string? ProjectId, string? ProjectSlug, string Excerpt, SourceSpan? Span);

/// <summary>
/// What an orchestrator returns for one turn: a visible plan, an optional answer with citations,
/// and zero or more proposals. The plan never executes anything; the coordinator decides,
/// asks, and runs. <see cref="Understood"/> false means the producer could not interpret the
/// instruction, which lets a deterministic front end hand over to a model.
/// </summary>
public sealed record TurnPlan(
    bool Understood,
    string Summary,
    IReadOnlyList<string> Steps,
    string? Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<Proposal> Proposals,
    string Producer,
    string? Raw = null)
{
    public static TurnPlan NotUnderstood(string producer, string summary) => new(false, summary, [], null, [], [], producer);
}

/// <summary>Live progress from inside a turn. Implementations must be safe to call from any thread.</summary>
public interface ITurnSink
{
    void Progress(string text);
    void ToolCalled(string tool, IReadOnlyDictionary<string, string> args);
    void ToolReturned(string tool, bool ok, string summary, int items);
    void ModelRequested(string host, string model, int promptChars, int sources);
    void ModelResponded(bool ok, int chars, long elapsedMs, string? error);
}

public sealed class TurnContext
{
    public required ToolBroker Tools { get; init; }
    public required ITurnSink Sink { get; init; }
    public required ProjectRegistry Registry { get; init; }
    public required WorkspaceRoots Roots { get; init; }
    public required IDraftNoteStore Drafts { get; init; }
    public required OrchestratorSettings Settings { get; init; }
}

public interface IOrchestrator
{
    string Name { get; }
    Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken);
}

/// <summary>Deterministic grammar first; a fallback (the model) only for what the grammar does not understand.</summary>
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
        context.Sink.Progress($"{_primary.Name} did not understand the instruction; asking {_fallback.Name}");
        return await _fallback.PlanAsync(request, context, cancellationToken).ConfigureAwait(false);
    }
}
