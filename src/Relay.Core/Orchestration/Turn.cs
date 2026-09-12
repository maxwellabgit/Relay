using Relay.Core.Config;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Tasks;
using Relay.Core.Workspaces;

namespace Relay.Core.Orchestration;

/// <summary>A pointer from an answer back to the words that support it.</summary>
public sealed record Citation(string Kind, string Id, string? ProjectId, string? ProjectSlug, string Excerpt, SourceSpan? Span);

/// <summary>
/// What one task has come to: the visible feed of what was done, the answer with what it stands on,
/// the knowledge state reached, and zero or more proposals. It executes nothing itself; the
/// coordinator decides, asks, and runs. Rebuilt on every step of the mind's loop, so it is also what
/// the window shows while a task is still running.
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
    /// <summary>For check tasks: true when the stated fact agrees with what is stored, false when it conflicts, null when the mind could not tell.</summary>
    bool? Consistent = null);

/// <summary>Live progress from inside a task. Implementations must be safe to call from any thread.</summary>
public interface ITurnSink
{
    void Progress(string text);
    void ToolCalled(string tool, IReadOnlyDictionary<string, string> args);
    void ToolReturned(string tool, bool ok, string summary, int items);
    void ModelRequested(string host, string model, int promptChars, int sources);
    void ModelResponded(bool ok, int chars, long elapsedMs, string? error, int promptTokens = 0, int completionTokens = 0);
}

/// <summary>
/// What the read-only tools may reach, and where progress is reported. Built once per task (and once
/// per listening pass) and handed to the <see cref="ToolBroker"/>; the mind's own prompt context is
/// <see cref="Mind.MindContext"/>.
/// </summary>
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
    /// <summary>Names of configured external model profiles a package may be sent to.</summary>
    public IReadOnlyList<string> ExternalProfiles { get; init; } = [];
    /// <summary>The subset of <see cref="ExternalProfiles"/> whose host can search online when a request is approved with allowSearch.</summary>
    public IReadOnlyList<string> SearchProfiles { get; init; } = [];
}
