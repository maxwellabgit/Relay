using Relay.Core.Orchestration;

namespace Relay.Core.Mind;

/// <summary>One action the mind may propose, described for the prompt: the name, the target fields, and a note on when it applies.</summary>
public sealed record ActionDescriptor(string Action, string Target, string Note);

/// <summary>
/// What the mind knows besides the transcript: which tools, actions, delegate profiles and projects
/// exist, whether building is available, the user's response style, and any approved prompt fragment.
/// Built by the host per task; the same object is reused for every step of that task.
/// </summary>
public sealed class MindContext
{
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = ToolBroker.Descriptors;
    public IReadOnlyList<ActionDescriptor> Actions { get; init; } = ActionCatalog.ForDirect;
    /// <summary>Configured external profiles the mind may delegate to; empty means delegation is unavailable.</summary>
    public IReadOnlyList<string> DelegateProfiles { get; init; } = [];
    /// <summary>The subset of profiles whose host may search online when the user approves a request with allow_search.</summary>
    public IReadOnlyList<string> SearchProfiles { get; init; } = [];
    /// <summary>Whether the build move is available in this build. When false the mind is told to name the gap instead.</summary>
    public bool CanBuild { get; init; }
    /// <summary>"Name (id …, slug …)" per active project.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];
    /// <summary>The user's response style (compiled preferences).</summary>
    public string? ResponseStyle { get; init; }
    public int MaxAnswerChars { get; init; } = 1_200;
    /// <summary>Extra instructions approved through change sets (the 'mind' prompt fragment).</summary>
    public string? PromptFragment { get; init; }
    /// <summary>When set, replaces the built-in constitution (the text of config\prompts\mind.md).</summary>
    public string? Constitution { get; init; }
    /// <summary>Related notes, excerpts or tasks found before the first step ("this connects to…"), one line each.</summary>
    public IReadOnlyList<string> Recall { get; init; } = [];
}

/// <summary>Everything one step is decided from: the task's transcript so far and the context.</summary>
public sealed record MindRequest(string TaskId, string Origin, IReadOnlyList<Observation> Transcript, MindContext Context, DateTimeOffset At, int StepIndex);

/// <summary>
/// The decision-maker. One call per step, one <see cref="MindStep"/> back. Implementations: the model
/// behind <see cref="ModelMind"/>, or a <see cref="ScriptedMind"/> in tests and evaluation cases.
/// </summary>
public interface IMind
{
    string Name { get; }
    Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken);
}

/// <summary>The actions the mind may propose, as it is told about them. Policy still decides every one.</summary>
public static class ActionCatalog
{
    public static readonly IReadOnlyList<ActionDescriptor> All =
    [
        new(Policy.Actions.CreateProject, "{name, slug?}", "a new project folder"),
        new(Policy.Actions.ArchiveProject, "{projectId}", ""),
        new(Policy.Actions.RestoreProject, "{projectId}", ""),
        new(Policy.Actions.RenameProject, "{projectId, newName}", ""),
        new(Policy.Actions.DeleteProject, "{projectId, confirm:\"delete\"}", "only when the user explicitly asked to delete"),
        new(Policy.Actions.CreateDraftNote, "{text, type}", "a note kept in staging until it is filed; type: decision | fact | question | todo | idea"),
        new(Policy.Actions.RouteNote, "{noteId, projectId, type?, confidence}", "file a draft note into a project; confidence 0–1 is yours, the filing rule decides whether the user is asked"),
        new(Policy.Actions.ModifyNote, "{projectId, noteId, body?, status?, type?}", ""),
        new(Policy.Actions.SupersedeNote, "{projectId, noteId, newText, type?, sourceExcerptId?}", "replace a stored decision with corrected text without erasing the old one"),
        new(Policy.Actions.MoveNote, "{projectId, noteId, toProjectId}", ""),
        new(Policy.Actions.LaunchWorker, "{projectId, task:\"summarize\", objective}", "a sandboxed worker over one project's files"),
        new(Policy.Actions.ApplyPatch, "{projectId, runId, output, destination}", "apply a finished worker's output"),
        new(Policy.Actions.ExportBackup, "{path?}", ""),
        new(Policy.Actions.UpdatePreference, "{key, value, benefit, permissions, scope, acceptance}", "how Relay itself behaves; keys: response.verbosity | response.promptLine | display.alwaysShow | display.stopShowing | display.maxAlertsPer10Minutes | display.maxResultsPer5Minutes | display.cooldownSeconds | filing.grant | filing.revoke | sources.allowOnlineSearch | retention.bufferSeconds | retention.excerptMaxSeconds"),
        new(Policy.Actions.UpdatePrompt, "{name:\"mind\", content, benefit, permissions, scope, acceptance}", "an approved addition to your own instructions"),
    ];

    /// <summary>Everything, for a task the user asked for directly.</summary>
    public static readonly IReadOnlyList<ActionDescriptor> ForDirect = All;

    /// <summary>For an overheard task: no deletion, no self-change, no external request without the user's own words.</summary>
    public static readonly IReadOnlyList<ActionDescriptor> ForObserved = All.Where(a => !Policy.Actions.DirectOnly.Contains(a.Action)).ToList();
}
