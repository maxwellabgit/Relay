using System.Text.Json;
using Relay.Core.Attention;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.External;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.SelfChange;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Stream;
using Relay.Core.Tasks;
using Relay.Core.Workspaces;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Core.Session;

/// <summary>Dependencies of the task engine; bundled so the coordinator's constructor stays readable.</summary>
public sealed class CoordinatorServices
{
    public required ProjectRegistry Registry { get; init; }
    public required WorkspaceRoots Roots { get; init; }
    /// <summary>The planner. Replaced when the user changes orchestrator/model settings; read at the start of each task.</summary>
    public required IOrchestrator Orchestrator { get; set; }
    /// <summary>
    /// Relay's one mind (docs/09): it runs every task and reads every conversation. Replaced when the user
    /// changes model settings; read at the start of each task and each listening pass. Null when no model answers.
    /// </summary>
    public Mind.IMind? Mind { get; set; }
    /// <summary>Weights and thresholds of every decision between paths; loaded from config\decisions.json.</summary>
    public Decisions.DecisionSet Decisions { get; set; } = Relay.Core.Decisions.DecisionSet.Default();
    /// <summary>Where finished tasks leave their usage line; null keeps no usage data.</summary>
    public Usage.UsageRecorder? Usage { get; init; }
    public required SearchIndex Index { get; init; }
    public IWorkerOperations? Workers { get; init; }
    /// <summary>Tools Relay builds for itself (docs/09, slice 6): store, sandbox runner, builder, promotion. Null when there is no worker host.</summary>
    public Tools.ToolRuntime? Tools { get; init; }
    public ExternalRuntime? External { get; init; }
    public ExcerptStore? Excerpts { get; init; }
    public ChangeSetStore? ChangeSets { get; init; }
    public PreferenceStore? Preferences { get; init; }
    /// <summary>Where the model API key lives; null when the host has no protected store.</summary>
    public Model.ISecretStore? Secrets { get; init; }
    public IReadOnlyList<string> IndexProblems { get; init; } = [];
}

/// <summary>
/// The task engine: one pipeline for every origin. A task is created from a direct ask, something the
/// mind raised while listening, or a follow-up; it is planned with read-only tools, its proposals go through policy,
/// approvals (or standing grants), capabilities and the executor; it ends with a diagnostics record
/// and a presentation decided by the attention arbiter. The foreground task (a command capture or a
/// Projects-panel operation) also drives the global state machine so the primary surface follows it;
/// background tasks (observed, or asked while listening) run beside whatever the surface is doing.
/// </summary>
public sealed partial class SessionCoordinator : IExecutionSink
{
    private const int RecentTaskLimit = 40;

    private sealed class ProposalState
    {
        public required Proposal Proposal { get; set; }
        public required Decision Decision { get; set; }
        public required string Status { get; set; }   // pending | approved | rejected | denied | allowed | executing | executed | failed | skipped | edited | stopped
        public Capability? Capability { get; set; }
        public ExecutionResult? Result { get; set; }
        public string? GrantedBy { get; set; }
        /// <summary>What the user said when rejecting (the Reject reason), for the mind to read.</summary>
        public string? Note { get; set; }
    }

    private sealed class TaskState
    {
        public required string TaskId { get; init; }
        public required TaskOrigin Origin { get; init; }
        public required TaskKind Kind { get; init; }
        public required string Lane { get; init; }     // command | user_operation | ask | observed | dialogue
        public required string CaptureId { get; init; }
        public required string SourceEventId { get; init; }
        public required string Instruction { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public bool Foreground { get; init; }
        /// <summary>True when the task's words came from listening rather than from something the user typed: observed tasks and their follow-ups. Governs what the ledger may quote.</summary>
        public bool Overheard { get; init; }
        public string? ExcerptId { get; init; }
        /// <summary>For a follow-up that summarises an external result: the stored artifact it is about.</summary>
        public string? ArtifactId { get; init; }
        public string? ParentTaskId { get; init; }
        public string? MergeKey { get; init; }
        public string? Topic { get; init; }
        public string? ProjectHint { get; init; }
        public string? Title { get; init; }
        public string? Why { get; init; }
        public double Confidence { get; init; } = 1;
        public string? WatchedTerm { get; init; }
        public Presentation? Suggested { get; init; }
        public TaskStatus Status { get; set; } = TaskStatus.Planning;
        public TurnPlan? Plan { get; set; }
        public List<ProposalState> Proposals { get; } = new();
        public List<ToolCallRecord> ToolCalls { get; } = new();
        public List<ModelCallRecord> ModelCalls { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public IDisposable? Timeout { get; set; }
        public bool StopRequested { get; set; }
        public ProposalState? PendingOperation { get; set; }
        public string? Outcome { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public Presentation Presentation { get; set; } = Presentation.None;
        public string? PresentationReason { get; set; }
        public string? UserResponse { get; set; }
        public bool IsLive => Status is TaskStatus.Planning or TaskStatus.AwaitingApproval or TaskStatus.Executing;
        public int PromptTokens => ModelCalls.Sum(m => m.PromptTokens);
        public int CompletionTokens => ModelCalls.Sum(m => m.CompletionTokens);

        // Mind mode (docs/09): the loop that runs this task and what the coordinator owes it.
        public Mind.TaskLoop? Loop { get; set; }
        public MindHost? Host { get; set; }
        /// <summary>True from the moment the loop is asked to step until its returned task is observed on the coordinator thread.</summary>
        public bool LoopBusy { get; set; }
        /// <summary>Consequences that arrived while the loop was stepping; delivered in order when it returns.</summary>
        public Queue<Mind.MoveOutcome> PendingOutcomes { get; } = new();
        /// <summary>Proposal ids whose approval, rejection or automatic allowance the loop has already been told about.</summary>
        public HashSet<string> ObservedApprovals { get; } = new(StringComparer.Ordinal);
        /// <summary>The tool build in flight for this task (slice 6), if any.</summary>
        public BuildState? Build { get; set; }
    }

    private readonly List<TaskState> _tasks = new();
    private TaskState? _lastForeground;
    private readonly List<ReviewItem> _recoveryReview = new();
    private ExcerptStore? _excerpts;
    private ChangeSetStore? _changeSets;
    private PreferenceStore? _preferences;
    private AttentionArbiter? _arbiter;
    private CompiledPreferences? _compiledPreferences;

    private ExcerptStore Excerpts => _excerpts ??= _services.Excerpts ?? new ExcerptStore(_root);
    private ChangeSetStore ChangeSets => _changeSets ??= _services.ChangeSets ?? new ChangeSetStore(_root);
    private PreferenceStore PreferenceStore => _preferences ??= _services.Preferences ?? new PreferenceStore(_root, ChangeSets);
    private AttentionArbiter Arbiter => _arbiter ??= new AttentionArbiter(() => Preferences);
    private CompiledPreferences Preferences => _compiledPreferences ??= PreferenceStore.Compiled();
    private TaskState? Foreground => _tasks.FirstOrDefault(t => t.Foreground && t.IsLive);

    public string OrchestratorMode => _settings.Orchestrator.Mode;
    public bool OrchestratorEnabled => _settings.Orchestrator.Mode != OrchestratorSettings.Off;

    LedgerRecord? IExecutionSink.Record(string type, object data) => Append(type, data);

    private PolicyWorld WorldFor(TaskState? task, bool forExecution = false) => new()
    {
        PlannedProjects = task is null || forExecution ? [] : task.Proposals
            .Where(p => p.Proposal.Action == Actions.CreateProject && p.Status is "pending" or "allowed" or "approved" or "executing")
            .Select(p => p.Proposal.Target.GetValueOrDefault("slug") ?? Slug.From(p.Proposal.Target.GetValueOrDefault("name") ?? ""))
            .Where(s => s.Length > 0)
            .ToList(),
        Registry = _services.Registry,
        Roots = _services.Roots,
        DataRoot = _root,
        DraftNoteExists = id => _notes.Read(id) is not null,
        ProjectNoteExists = (projectId, noteId) => _services.Registry.ById(projectId) is { } p && Directory.Exists(p.RootPath) && ProjectNoteStore.Find(p.RootPath, noteId) is not null,
        WorkersEnabled = _settings.Workers.Enabled && _services.Workers is not null,
        AgentRunHasOutput = _services.Workers is null ? null : runId => Directory.Exists(Path.Combine(_root.AgentsDirectory, runId, "out")),
        ExternalProfiles = _services.External?.ProfileNames ?? [],
        OnlineSearchGranted = Preferences.AllowOnlineSearch,
        ReferenceExists = ReferenceExists,
        PromptFragments = SelfChangeRuntime.PromptNames,
        Origin = task?.Origin,
        Kind = task?.Kind,
    };

    private PolicyWorld World => WorldFor(null);

    private bool ReferenceExists(string id)
    {
        if (!Ulid.IsValid(id)) return false;
        if (_notes.Read(id) is not null) return true;
        if (Excerpts.Read(id) is not null) return true;
        if (_services.External?.ReadArtifact(id) is not null) return true;
        if (_services.Index.Search(id, null, 1).Any(h => h.Id == id)) return true;
        foreach (var p in _services.Registry.Active)
            if (Directory.Exists(p.RootPath) && ProjectNoteStore.Find(p.RootPath, id) is not null) return true;
        return false;
    }

    private ToolSources ToolSources => new()
    {
        Registry = _services.Registry,
        Drafts = _notes,
        Index = _services.Index,
        Excerpts = Excerpts,
        ReadArtifact = _services.External is null ? null : _services.External.ReadArtifact,
        Preferences = () => Preferences,
    };

    // ----------------------------------------------------------------------------------------
    // Creating tasks
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// What lane a direct ask is filed under. The mind works this out for itself from the words, so
    /// under it every ask starts as something to answer; the grammar needs the lane up front.
    /// </summary>
    private TaskKind DirectKind(string text) => MindMode ? TaskKind.Answer : Orchestration.RuleBasedOrchestrator.DirectKind(text);

    /// <summary>The command capture became a task: direct, foreground, drives the state machine.</summary>
    private void StartCommandTask(Captures.CaptureDraft draft, string sourceEventId)
    {
        var task = NewTask(TaskOrigin.Direct, DirectKind(draft.Text), "command", draft.CaptureId, sourceEventId, draft.Text, foreground: true, title: Truncate(draft.Text, 80));
        _lastForeground = task;
        if (Append(EventTypes.TaskCreated, TaskCreatedPayload(task, chars: draft.Text.Length)) is null) return;
        PersistTask(task);
        if (!Apply(Trigger.BeginPlanning).Accepted) return;
        BeginPlanning(task);
    }

    /// <summary>
    /// The ask box: a direct instruction typed or dictated without the command chord. When nothing
    /// else holds the surface the ask is the foreground task; while listening it runs beside the
    /// stream and its answer surfaces as a card. The instruction text is kept: it is intent, not conversation.
    /// </summary>
    public bool SubmitDirect(string text)
    {
        if (_shutDown) return false;
        text = (text ?? "").Trim();
        if (text.Length == 0) { _notice = "Type or dictate something to ask."; Notify(); return false; }
        if (_state is RelayState.Locked or RelayState.Starting or RelayState.Failed) { _notice = "Resolve the current incident first."; Notify(); return false; }
        if (!OrchestratorEnabled) { _notice = "The orchestrator is off; enable it in settings to ask."; Notify(); return false; }
        // Mind mode: a task waiting for the user's answer gets the typed text as its reply, in the same box.
        if (Foreground is { Loop: not null } waitingTask && waitingTask.Loop.WaitingFor == Mind.Waits.User) return AnswerMind(waitingTask.TaskId, text);
        var now = _clock.UtcNow;
        var foreground = _state is RelayState.Idle or RelayState.Completed;
        if (foreground && Foreground is not null) foreground = false;
        var source = Append(EventTypes.AskRecorded, new { text, chars = text.Length, whileListening = _state == RelayState.NoteCapture, foreground });
        if (source is null) { Notify(); return false; }
        var task = NewTask(TaskOrigin.Direct, DirectKind(text), "ask", "", source.Id, text, foreground, title: Truncate(text, 80));
        if (foreground)
        {
            _receiptTimer?.Dispose();
            _receipt = null;
            _lastForeground = task;
        }
        if (Append(EventTypes.TaskCreated, TaskCreatedPayload(task, chars: text.Length)) is null) return false;
        PersistTask(task);
        if (foreground)
        {
            if (_state == RelayState.Completed) Apply(Trigger.Dismiss);
            if (!Apply(Trigger.BeginPlanning).Accepted) { _tasks.Remove(task); Notify(); return false; }
        }
        BeginPlanning(task);
        Notify();
        return true;
    }

    /// <summary>A follow-up inherits the parent's origin scope but is its own task with its own record.</summary>
    private void StartFollowUpTask(TaskState parent, TaskKind kind, string instruction, string title, string sourceEventId, string? artifactId = null)
    {
        var task = NewTask(TaskOrigin.Dialogue, kind, "dialogue", parent.CaptureId, sourceEventId, instruction, foreground: false, parentTaskId: parent.TaskId, title: title, mergeKey: parent.MergeKey, topic: parent.Topic, projectHint: parent.ProjectHint, artifactId: artifactId);
        if (Append(EventTypes.TaskCreated, TaskCreatedPayload(task, chars: instruction.Length)) is null) return;
        PersistTask(task);
        BeginPlanning(task);
    }

    private TaskState NewTask(TaskOrigin origin, TaskKind kind, string lane, string captureId, string sourceEventId, string instruction, bool foreground,
        string? excerptId = null, string? parentTaskId = null, string? mergeKey = null, string? topic = null, string? projectHint = null, string? title = null,
        string? why = null, double confidence = 1, Presentation? suggested = null, string? watchedTerm = null, string? artifactId = null)
    {
        var task = new TaskState
        {
            TaskId = Ulid.NewUlid(_clock.UtcNow),
            Origin = origin,
            Kind = kind,
            Lane = lane,
            CaptureId = captureId,
            SourceEventId = sourceEventId,
            Instruction = instruction,
            StartedAt = _clock.UtcNow,
            Foreground = foreground,
            Overheard = origin == TaskOrigin.Observed || (parentTaskId is not null && _tasks.FirstOrDefault(t => t.TaskId == parentTaskId)?.Overheard == true),
            ExcerptId = excerptId,
            ArtifactId = artifactId,
            ParentTaskId = parentTaskId,
            MergeKey = mergeKey,
            Topic = topic,
            ProjectHint = projectHint,
            Title = title,
            Why = why,
            Confidence = confidence,
            Suggested = suggested,
            WatchedTerm = watchedTerm,
        };
        _tasks.Insert(0, task);
        TrimTasks();
        return task;
    }

    /// <summary><paramref name="raisedBy"/> is the mind that raised the task from a listening pass; it is null for everything the user asked for directly.</summary>
    private object TaskCreatedPayload(TaskState task, int chars, string? raisedBy = null) => new
    {
        taskId = task.TaskId,
        origin = task.Origin.Wire(),
        kind = task.Kind.Wire(),
        lane = task.Lane,
        foreground = task.Foreground,
        overheard = task.Overheard,
        captureId = task.CaptureId.Length > 0 ? task.CaptureId : null,
        sourceEventId = task.SourceEventId.Length > 0 ? task.SourceEventId : null,
        excerptId = task.ExcerptId,
        parentTaskId = task.ParentTaskId,
        title = Guarded(task, task.Title),
        why = Guarded(task, task.Why),
        confidence = task.Confidence,
        mergeKey = Guarded(task, task.MergeKey),
        chars,
        raisedBy,
        planner = PlannerName,
    };

    // ----------------------------------------------------------------------------------------
    // What the ledger may say about words that were only overheard
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The ledger never carries the words of the room. Segments enter it as hashes and excerpts as ids; but what the
    /// mind or a planner writes about an overheard task (its title, summary, answer, tool arguments, raw model output)
    /// can quote those words, so for such tasks the ledger records a fingerprint (length and a SHA-256 prefix) and the
    /// task record under tasks\ keeps the text, under the same retention as the excerpt it cites. Tasks that began
    /// from something the user typed are recorded in full: those words are the user's own instruction.
    /// </summary>
    private static string? Guarded(TaskState task, string? text) => text is null || !task.Overheard ? text : Fingerprint(text);

    private static string Fingerprint(string text) => Withheld.Fingerprint(text);

    private static IReadOnlyList<string> Guarded(TaskState task, IReadOnlyList<string> texts) => task.Overheard ? texts.Select(Fingerprint).ToList() : texts;

    private static IReadOnlyDictionary<string, string> Guarded(TaskState task, IReadOnlyDictionary<string, string> map)
        => task.Overheard ? map.ToDictionary(kv => kv.Key, kv => Fingerprint(kv.Value), StringComparer.Ordinal) : map;

    /// <summary>A proposal target for the ledger: for an overheard task the prose is fingerprinted and the references kept (<see cref="Withheld.Target"/>).</summary>
    private static IReadOnlyDictionary<string, string> GuardedTarget(TaskState task, IReadOnlyDictionary<string, string> target)
        => task.Overheard ? Withheld.Target(target) : target;

    private void TrimTasks()
    {
        var finished = _tasks.Where(t => !t.IsLive).Skip(RecentTaskLimit).ToList();
        foreach (var t in finished) _tasks.Remove(t);
    }

    // ----------------------------------------------------------------------------------------
    // Planning
    // ----------------------------------------------------------------------------------------

    private void BeginPlanning(TaskState task)
    {
        if (MindMode) { BeginMindLoop(task); return; }
        task.Timeout = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Orchestrator.PlanningTimeoutMs), () =>
        {
            if (!_tasks.Contains(task) || task.Status != TaskStatus.Planning) return;
            FailTask(task, "planning_timeout", $"The planner did not answer within {_settings.Orchestrator.PlanningTimeoutMs / 1000}s.", null);
            task.Cts.Cancel(); // after the task is closed, so the cancelled continuation finds nothing to do
            Notify();
        });

        var request = new TurnRequest(task.TaskId, task.CaptureId, task.SourceEventId, task.Instruction, task.StartedAt, task.Origin, task.Kind, task.ExcerptId, task.ArtifactId);
        var sink = new TaskSink(this, task);
        var context = new TurnContext
        {
            Tools = new ToolBroker(ToolSources, sink, _settings.Orchestrator.MaxToolCalls),
            Sink = sink,
            Registry = _services.Registry,
            Roots = _services.Roots,
            Drafts = _notes,
            Settings = _settings.Orchestrator,
            CompletedRuns = projectId => Agents.AgentRunStatus.All(_root).Where(s => s.ProjectId == projectId && s.State == "completed" && !s.Applied).ToList(),
            Preferences = Preferences,
            ExternalProfiles = _services.External?.ProfileNames ?? [],
            SearchProfiles = _services.External?.SearchProfileNames ?? [],
            PromptFragment = SelfChange?.PromptFragment("planner"),
        };

        Task<TurnPlan> planning;
        try { planning = _services.Orchestrator.PlanAsync(request, context, task.Cts.Token); }
        catch (Exception ex) { planning = Task.FromException<TurnPlan>(ex); }
        planning.ContinueWith(t => _scheduler.Post(() => OnPlanFinished(task, t)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnPlanFinished(TaskState task, Task<TurnPlan> planning)
    {
        if (!_tasks.Contains(task) || task.Status != TaskStatus.Planning) return; // cancelled, timed out, or failed meanwhile
        task.Timeout?.Dispose();
        task.Timeout = null;

        if (planning.IsCanceled) return;
        if (planning.IsFaulted)
        {
            var ex = planning.Exception?.GetBaseException() ?? new InvalidOperationException("Planning failed.");
            FailTask(task, "planning_failed", ex.Message, ex.ToString());
            Notify();
            return;
        }

        var plan = planning.Result;
        task.Plan = plan;
        if (Append(EventTypes.TaskPlanned, new
        {
            taskId = task.TaskId,
            producer = plan.Producer,
            understood = plan.Understood,
            summary = Guarded(task, plan.Summary),
            steps = Guarded(task, plan.Steps),
            answer = Guarded(task, plan.Answer),
            citations = plan.Citations.Select(c => new { c.Kind, c.Id, c.ProjectSlug, span = c.Span }),
            consistent = plan.Consistent,
            knowledge = task.Overheard && plan.Knowledge is { } k
                ? new { known = k.Known.Count, missing = k.Missing.Count, capabilityGap = k.CapabilityGap, summary = Guarded(task, k.Summary) }
                : (object?)plan.Knowledge,
            proposals = plan.Proposals.Count,
            toolCalls = task.ToolCalls.Count,
            modelCalls = task.ModelCalls.Count,
            promptTokens = task.PromptTokens,
            completionTokens = task.CompletionTokens,
            raw = Guarded(task, plan.Raw),
        }) is null) return;

        if (!plan.Understood)
        {
            task.Plan = plan with
            {
                Answer = plan.Answer ?? (task.Origin != TaskOrigin.Direct ? null
                    : _settings.Model.Enabled || plan.Producer.StartsWith(Model.ModelOrchestrator.ProducerPrefix, StringComparison.Ordinal)
                        ? "The instruction could not be interpreted: " + plan.Summary
                        : "The built-in grammar did not understand that, and RELAY0's model is not enabled. Try: \"create project <name>\", \"archive project <name>\", \"list projects\", \"remember that …\", \"file the last note under <project>\", \"what did I say about …\", \"summarize project <name>\", \"export a backup\"."),
            };
        }

        // Prerequisites are decided before their dependents so a dependent can name a project its prerequisite creates.
        foreach (var proposal in plan.Proposals.OrderBy(p => p.Dependencies.Count)) ReceiveProposal(task, proposal);
        AdvanceTask(task);
        Notify();
    }

    private void ReceiveProposal(TaskState task, Proposal proposal)
    {
        PersistProposal(proposal);
        // A planner's reason and the prose in its target (note text, an objective, a title) can quote the overheard words, so they are guarded like its answer; ids, slugs and flags stay legible.
        Append(EventTypes.ProposalReceived, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, reason = Guarded(task, proposal.Reason), target = GuardedTarget(task, proposal.Target), sourceEventIds = proposal.SourceEventIds, expectedEffects = Guarded(task, proposal.ExpectedEffects), risk = proposal.Risk, requiresApproval = proposal.RequiresApproval, proposedBy = proposal.ProposedBy, dependsOn = proposal.Dependencies, hash = proposal.Hash() });
        var decision = PolicyEngine.Decide(proposal, WorldFor(task));
        Append(EventTypes.ProposalDecided, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = decision.Outcome.ToString(), tier = decision.Tier.ToString(), reasons = decision.Reasons, target = GuardedTarget(task, decision.NormalizedTarget) });
        var status = decision.Outcome switch
        {
            DecisionOutcome.Allow => "allowed",
            DecisionOutcome.NeedsApproval => "pending",
            _ => "denied",
        };
        var ps = new ProposalState { Proposal = proposal, Decision = decision, Status = status };

        // A standing grant is a prior approval for a narrow, repeatable operation; it never covers the actions that need fresh words each time.
        if (status == "pending" && !Actions.NeverGranted.Contains(proposal.Action) && proposal.ProposedBy != Producers.User)
        {
            var target = decision.NormalizedTarget.Count > 0 ? decision.NormalizedTarget : proposal.Target;
            var grant = Preferences.Grants.FirstOrDefault(g => g.Covers(proposal.Action, target));
            if (grant is not null)
            {
                Append(EventTypes.GrantApplied, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, grantId = grant.GrantId, projectId = grant.ProjectId, noteType = grant.NoteType, proposalHash = proposal.Hash() });
                ps.Status = "approved";
                ps.GrantedBy = grant.GrantId;
                ps.Capability = _capabilities.Issue(proposal, _clock.UtcNow);
            }
        }
        task.Proposals.Add(ps);
    }

    private static bool DependenciesMet(TaskState task, ProposalState ps)
        => ps.Proposal.Dependencies.All(id => task.Proposals.Any(p => p.Proposal.ProposalId == id && p.Status == "executed"));

    private static bool DependenciesDead(TaskState task, ProposalState ps)
        => ps.Proposal.Dependencies.Any(id => task.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == id) is null or { Status: "rejected" or "denied" or "failed" or "skipped" or "edited" or "stopped" });

    /// <summary>Why a pending proposal cannot be approved: the prerequisite that will not run, by name and fate.</summary>
    private static string BlockedReason(TaskState task, ProposalState ps)
    {
        var dead = ps.Proposal.Dependencies.Select(id => task.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == id))
            .Where(p => p is null or { Status: "rejected" or "denied" or "failed" or "skipped" or "edited" or "stopped" }).ToList();
        var names = dead.Select(p => p is null ? "a missing prerequisite" : $"{ProposalText.Describe(p.Proposal, p.Decision).Title} ({p.Status})");
        return $"This depends on {string.Join(" and ", names)}. Approve the prerequisite first, or reject this one.";
    }

    /// <summary>Moves the task to approval, execution, or completion depending on what the proposals need.</summary>
    private void AdvanceTask(TaskState task)
    {
        if (task.Loop is not null) { AdvanceMindTask(task); return; }
        if (task.Proposals.Any(p => p.Status == "pending"))
        {
            var entering = task.Status != TaskStatus.AwaitingApproval;
            task.Status = TaskStatus.AwaitingApproval;
            PersistTask(task);
            if (task.Foreground && _state == RelayState.Planning) Apply(Trigger.ApprovalRequired);
            if (entering && !task.Foreground) PresentTask(task, interim: true);
            return;
        }
        if (task.Proposals.Any(p => p.Status is "allowed" or "approved"))
        {
            task.Status = TaskStatus.Executing;
            PersistTask(task);
            if (task.Foreground && _state is RelayState.Planning or RelayState.AwaitingApproval) Apply(Trigger.BeginExecution);
            RunExecutionQueue(task);
            return;
        }
        FinishTask(task);
    }

    private void RunExecutionQueue(TaskState task)
    {
        while (true)
        {
            var runnable = task.Proposals.Where(p => p.Status is "allowed" or "approved").ToList();
            if (runnable.Count == 0) break;
            if (task.StopRequested)
            {
                foreach (var p in runnable) p.Status = "skipped";
                break;
            }
            var next = runnable.FirstOrDefault(p => DependenciesMet(task, p));
            if (next is null)
            {
                foreach (var p in runnable.Where(p => DependenciesDead(task, p)))
                {
                    p.Status = "skipped";
                    p.Result = ExecutionResult.Fail("A prerequisite operation did not execute.");
                    Append(EventTypes.ExecutionFailed, new { taskId = task.TaskId, proposalId = p.Proposal.ProposalId, action = p.Proposal.Action, error = p.Result.Error, stage = "dependency" });
                }
                if (task.Proposals.Any(p => p.Status is "allowed" or "approved")) { foreach (var p in task.Proposals.Where(p => p.Status is "allowed" or "approved")) p.Status = "skipped"; }
                break;
            }
            next.Status = "executing";
            next.Capability ??= _capabilities.Issue(next.Proposal, _clock.UtcNow);
            var result = _executor.Execute(next.Proposal, next.Capability, WorldFor(task, forExecution: true), task.TaskId, this, overheard: task.Overheard);
            next.Result = result;
            if (result.Status == ExecutionStatus.Pending)
            {
                task.PendingOperation = next;
                PersistTask(task);
                Notify();
                return; // resumed by CompletePendingOperation
            }
            next.Status = result.Status == ExecutionStatus.Completed ? "executed" : "failed";
            RefreshIndexAfter(next);
            Notify();
        }
        FinishTask(task);
    }

    /// <summary>Called (via Post) when an asynchronous operation (a worker run, an external request) has finished.</summary>
    public void CompletePendingOperation(string proposalId, ExecutionResult result)
    {
        var task = _tasks.FirstOrDefault(t => t.PendingOperation?.Proposal.ProposalId == proposalId);
        if (task?.PendingOperation is null) return;
        var op = task.PendingOperation;
        task.PendingOperation = null;
        _executor.Complete(op.Proposal, task.TaskId, result, this);
        op.Result = result;
        // A worker killed because the user asked for a stop is not a failure of the task; it is the stop working.
        op.Status = result.Status == ExecutionStatus.Completed ? "executed" : task.StopRequested ? "stopped" : "failed";
        if (op.Status == "executed") RefreshIndexAfter(op);
        if (task.Loop is not null) { OnMindOperationCompleted(task, op, result); Notify(); return; }
        if (op.Status == "executed" && op.Proposal.Action == Actions.ModelRequest && result.Outputs.TryGetValue("artifactId", out var artifactId))
        {
            var objective = op.Proposal.Target.GetValueOrDefault("objective") ?? task.Instruction;
            var record = Append(EventTypes.TaskUserResponse, new { taskId = task.TaskId, proposalId, response = "external_returned", artifactId });
            StartFollowUpTask(task, TaskKind.Research,
                $"An external model ({result.Outputs.GetValueOrDefault("profile")}) returned artifact {artifactId} for this objective: \"{objective}\". Read the artifact (read_artifact), summarize what it established in the user's preferred style, cite the artifact, and name anything it could not determine.",
                "External result: " + Truncate(objective, 60), record?.Id ?? task.SourceEventId, artifactId);
        }
        if (task.Status == TaskStatus.Executing) RunExecutionQueue(task);
        Notify();
    }

    private void FinishTask(TaskState task)
    {
        task.Timeout?.Dispose();
        task.Timeout = null;
        var executed = task.Proposals.Count(p => p.Status == "executed");
        var failed = task.Proposals.Count(p => p.Status == "failed");
        var denied = task.Proposals.Count(p => p.Status == "denied");
        var rejected = task.Proposals.Count(p => p.Status is "rejected" or "edited");
        var skipped = task.Proposals.Count(p => p.Status == "skipped");
        var stopped = task.Proposals.Count(p => p.Status == "stopped");
        task.Outcome = failed > 0 ? "failed" : stopped > 0 ? "stopped" : executed > 0 ? "executed" : denied > 0 && task.Proposals.Count == denied ? "denied" : rejected > 0 && executed == 0 ? "rejected" : task.Plan?.Answer is not null ? "answered" : "completed";
        task.Status = failed > 0 ? TaskStatus.Failed : TaskStatus.Completed;
        task.CompletedAt = _clock.UtcNow;

        Append(EventTypes.TaskCompleted, new
        {
            taskId = task.TaskId, origin = task.Origin.Wire(), kind = task.Kind.Wire(), lane = task.Lane, outcome = task.Outcome,
            proposals = task.Proposals.Count, executed, failed, denied, rejected, skipped, stopped, stopRequested = task.StopRequested,
            toolCalls = task.ToolCalls.Count, modelCalls = task.ModelCalls.Count, promptTokens = task.PromptTokens, completionTokens = task.CompletionTokens,
            wallMs = (long)(task.CompletedAt.Value - task.StartedAt).TotalMilliseconds,
        });
        PresentTask(task, interim: false);
        WriteDiagnostics(task);

        if (!task.Foreground) return;

        if (failed > 0)
        {
            var firstError = task.Proposals.First(p => p.Status == "failed");
            _incident = new IncidentInfo("execution_failed", $"{firstError.Proposal.Action} failed", firstError.Result?.Error ?? "unknown", _clock.UtcNow, null);
            _retryable = false;
            Apply(_state == RelayState.Executing ? Trigger.ExecutionFailed : Trigger.PlanFailed);
            return;
        }

        var receipt = task.Outcome switch
        {
            "executed" => $"Done · {executed} operation(s) executed" + (skipped > 0 ? $", {skipped} skipped" : ""),
            "stopped" => "Stopped · work terminated at your request, partial output kept in staging" + (executed > 0 ? $"; {executed} earlier operation(s) stand" : ""),
            "denied" => "Nothing ran · every proposal was denied by policy",
            "rejected" => "Nothing ran · proposals rejected",
            "answered" => "Answered · no changes made",
            _ => "Handled · no changes made",
        };
        switch (_state)
        {
            case RelayState.Executing: Apply(Trigger.ExecutionSucceeded); break;
            case RelayState.AwaitingApproval: Apply(Trigger.AllRejected); break;
            case RelayState.Planning: Apply(Trigger.PlanReady); break;
        }
        ShowReceipt(receipt);
    }

    private void FailTask(TaskState task, string kind, string summary, string? detail)
    {
        task.Timeout?.Dispose();
        task.Timeout = null;
        task.Outcome = "failed";
        task.Status = TaskStatus.Failed;
        task.CompletedAt = _clock.UtcNow;
        Append(EventTypes.TaskFailed, new { taskId = task.TaskId, origin = task.Origin.Wire(), kind = task.Kind.Wire(), failure = kind, error = summary, pendingOperation = task.PendingOperation?.Proposal.ProposalId });
        PresentTask(task, interim: false);
        WriteDiagnostics(task);
        if (!task.Foreground) return;
        _incident = new IncidentInfo(kind, summary, detail ?? summary, _clock.UtcNow, null);
        _retryable = false;
        Apply(_state == RelayState.Executing ? Trigger.ExecutionFailed : Trigger.PlanFailed);
    }

    private void CancelForegroundTask()
    {
        var task = Foreground;
        if (task is null) { Apply(Trigger.Cancel); return; }
        CancelTask(task, "user");
        Apply(Trigger.Cancel);
    }

    /// <summary>Cancels any task: planning is abandoned, pending proposals are rejected, a running operation is asked to stop.</summary>
    public bool CancelTask(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId && t.IsLive);
        if (task is null) { _notice = "That task is no longer running."; Notify(); return false; }
        if (task.Foreground) { CancelForegroundTask(); Notify(); return true; }
        CancelTask(task, "user");
        Notify();
        return true;
    }

    private void CancelTask(TaskState task, string reason)
    {
        switch (task.Status)
        {
            case TaskStatus.Planning:
                task.Timeout?.Dispose();
                task.Status = TaskStatus.Cancelled;
                task.Outcome = "cancelled";
                task.CompletedAt = _clock.UtcNow;
                Append(EventTypes.TaskCancelled, new { taskId = task.TaskId, stage = "planning", reason });
                WriteDiagnostics(task);
                task.Cts.Cancel();
                break;
            case TaskStatus.AwaitingApproval:
                foreach (var p in task.Proposals.Where(p => p.Status == "pending"))
                {
                    p.Status = "rejected";
                    Append(EventTypes.ApprovalRejected, new { taskId = task.TaskId, proposalId = p.Proposal.ProposalId, action = p.Proposal.Action, by = "user", reason = "task cancelled" });
                }
                task.Status = TaskStatus.Cancelled;
                task.Outcome = "cancelled";
                task.CompletedAt = _clock.UtcNow;
                Append(EventTypes.TaskCancelled, new { taskId = task.TaskId, stage = "awaiting_approval", reason });
                Arbiter.Resolve(task.TaskId, null, "", "", _clock.UtcNow);
                WriteDiagnostics(task);
                break;
            case TaskStatus.Executing:
                if (!task.StopRequested)
                {
                    task.StopRequested = true;
                    Append(EventTypes.ExecutionStopRequested, new { taskId = task.TaskId, pending = task.PendingOperation?.Proposal.ProposalId });
                    RequestWorkerStop?.Invoke(task.PendingOperation?.Proposal.ProposalId);
                    _services.External?.Stop(task.PendingOperation?.Proposal.ProposalId, "stop requested");
                }
                break;
        }
    }

    /// <summary>Set by the worker runtime so a stop request can terminate a running worker.</summary>
    public Action<string?>? RequestWorkerStop { get; set; }

    // ----------------------------------------------------------------------------------------
    // Presentation: the arbiter decides how much attention a finished task gets
    // ----------------------------------------------------------------------------------------

    private void PresentTask(TaskState task, bool interim)
    {
        var plan = task.Plan;
        var pendingCount = task.Proposals.Count(p => p.Status == "pending");
        var (title, detail) = TaskCardText(task);
        var input = new AttentionInput(task.TaskId, task.Origin, task.Kind, title, detail, task.MergeKey, task.Suggested, task.Confidence,
            !string.IsNullOrWhiteSpace(plan?.Answer), plan?.Consistent, plan?.Citations.Count ?? 0, pendingCount,
            task.Proposals.Count(p => p.Status == "executed"), task.Status == TaskStatus.Failed, task.WatchedTerm, _clock.UtcNow,
            task.Proposals.Count(p => p.Status is "rejected" or "edited"));
        if (task.Foreground)
        {
            // The foreground task is already on screen in Response (and its receipt); a card would show it twice.
            var (level, reason) = Arbiter.RankOnly(input);
            task.Presentation = level;
            task.PresentationReason = reason + " · shown in Response";
            Append(EventTypes.TaskPresented, new { taskId = task.TaskId, level = level.Wire(), reason, surface = "response", title = Guarded(task, title) });
            return;
        }
        var decision = Arbiter.Decide(input);
        task.Presentation = decision.Level;
        task.PresentationReason = decision.Reason;
        if (decision.Item is null)
        {
            Append(EventTypes.AttentionSuppressed, new { taskId = task.TaskId, level = decision.Level.Wire(), reason = decision.Reason });
            return;
        }
        if (decision.Merged) Append(EventTypes.TaskMerged, new { taskId = task.TaskId, itemId = decision.Item.ItemId, key = Guarded(task, decision.Item.Key), occurrences = decision.Item.Occurrences, into = decision.Item.TaskIds.FirstOrDefault() });
        Append(interim ? EventTypes.TaskPresented : EventTypes.AttentionShown, new { taskId = task.TaskId, itemId = decision.Item.ItemId, level = decision.Level.Wire(), reason = decision.Reason, merged = decision.Merged, title = Guarded(task, title) });
    }

    private (string Title, string Detail) TaskCardText(TaskState task)
    {
        var plan = task.Plan;
        var title = task.Kind switch
        {
            TaskKind.Check when plan?.Consistent == false => $"Conflict: {task.Title ?? task.Topic ?? "statement"}",
            TaskKind.Check => task.Title ?? "Checked",
            TaskKind.Resolve => task.Topic is not null ? task.Topic : task.Title ?? "Definition",
            TaskKind.Remember => task.Outcome == "executed" ? "Note filed" : "Note kept in the inbox",
            _ => task.Title ?? plan?.Summary ?? task.Instruction,
        };
        var detail = plan?.Answer ?? plan?.Summary ?? task.Instruction;
        if (task.Status == TaskStatus.Failed) detail = "Failed: " + (task.Proposals.FirstOrDefault(p => p.Status == "failed")?.Result?.Error ?? plan?.Summary ?? "unknown");
        if (task.Status == TaskStatus.AwaitingApproval) detail = $"{task.Proposals.Count(p => p.Status == "pending")} proposal(s) await your approval · " + (plan?.Summary ?? "");
        return (Truncate(title, 90), Truncate(detail, Preferences.MaxAnswerChars));
    }

    public void DismissAttention(string itemId)
    {
        if (Arbiter.Dismiss(itemId, _clock.UtcNow)) Append(EventTypes.AttentionDismissed, new { itemId });
        Notify();
    }

    /// <summary>The user reacted to a card (dismissed, opened, said "not needed"); recorded for the evaluation loop.</summary>
    public void RecordUserResponse(string taskId, string response)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null) return;
        task.UserResponse = response;
        Append(EventTypes.TaskUserResponse, new { taskId, response });
        WriteDiagnostics(task);
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // Approvals
    // ----------------------------------------------------------------------------------------

    private (TaskState Task, ProposalState Proposal)? FindPending(string proposalId)
    {
        foreach (var task in _tasks.Where(t => t.Status == TaskStatus.AwaitingApproval))
        {
            var ps = task.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == proposalId);
            if (ps is { Status: "pending" }) return (task, ps);
        }
        return null;
    }

    public void Approve(string proposalId)
    {
        if (FindPending(proposalId) is not var (task, ps)) { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        if (DependenciesDead(task, ps))
        {
            _notice = BlockedReason(task, ps);
            Append(EventTypes.ApprovalRefused, new { taskId = task.TaskId, proposalId, action = ps.Proposal.Action, reason = _notice, dependsOn = ps.Proposal.Dependencies });
            Notify();
            return;
        }
        var hash = ps.Proposal.Hash();
        if (Append(EventTypes.ApprovalGranted, new { taskId = task.TaskId, proposalId, action = ps.Proposal.Action, proposalHash = hash, by = "user", target = ps.Decision.NormalizedTarget }) is null) { Notify(); return; }
        ps.Status = "approved";
        ps.Capability = _capabilities.Issue(ps.Proposal, _clock.UtcNow);
        task.UserResponse ??= "approved";
        if (!task.Proposals.Any(p => p.Status == "pending")) Arbiter.Resolve(task.TaskId, null, "", "", _clock.UtcNow);
        AdvanceTask(task);
        Notify();
    }

    public void Reject(string proposalId, string? reason = null)
    {
        if (FindPending(proposalId) is not var (task, ps)) { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        if (Append(EventTypes.ApprovalRejected, new { taskId = task.TaskId, proposalId, action = ps.Proposal.Action, by = "user", reason = reason ?? "rejected" }) is null) { Notify(); return; }
        ps.Status = "rejected";
        ps.Note = reason;
        task.UserResponse ??= "rejected";
        if (!task.Proposals.Any(p => p.Status == "pending")) Arbiter.Resolve(task.TaskId, null, "", "", _clock.UtcNow);
        AdvanceTask(task);
        Notify();
    }

    /// <summary>Edit-and-re-propose: the edited target becomes a new proposal (attributed to the user) that is decided afresh; the original is recorded as edited.</summary>
    public void EditProposal(string proposalId, IReadOnlyDictionary<string, string> newTarget)
    {
        if (FindPending(proposalId) is not var (task, ps)) { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        var replacement = ps.Proposal.WithTarget(Ulid.NewUlid(_clock.UtcNow), new Dictionary<string, string>(newTarget, StringComparer.Ordinal));
        if (Append(EventTypes.ProposalEdited, new { taskId = task.TaskId, fromProposalId = proposalId, toProposalId = replacement.ProposalId, action = replacement.Action, oldTarget = ps.Proposal.Target, newTarget = replacement.Target }) is null) { Notify(); return; }
        ps.Status = "edited";
        // Dependents of the edited proposal now depend on its replacement.
        for (var i = 0; i < task.Proposals.Count; i++)
        {
            var dep = task.Proposals[i];
            if (dep.Proposal.Dependencies.Contains(proposalId))
                dep.Proposal = dep.Proposal with { DependsOn = dep.Proposal.Dependencies.Select(d => d == proposalId ? replacement.ProposalId : d).ToList() };
        }
        task.UserResponse ??= "edited";
        ReceiveProposal(task, replacement);
        RetargetDependents(task, ps.Proposal, replacement);
        AdvanceTask(task);
        Notify();
    }

    /// <summary>
    /// When the user renames a project inside an edited <c>create_project</c>, the pending moves that were
    /// heading for the old name follow it to the new one and are decided again; otherwise they would fail
    /// at run time against a project that was never created.
    /// </summary>
    private void RetargetDependents(TaskState task, Proposal edited, Proposal replacement)
    {
        if (edited.Action != Actions.CreateProject) return;
        var oldName = edited.Target.GetValueOrDefault("name") ?? "";
        var oldSlug = edited.Target.GetValueOrDefault("slug") ?? Slug.From(oldName);
        var newName = replacement.Target.GetValueOrDefault("name") ?? oldName;
        if (string.Equals(Slug.From(newName), oldSlug, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var dep in task.Proposals.Where(d => d.Status == "pending" && d.Proposal.Action == Actions.MoveNote && d.Proposal.Dependencies.Contains(replacement.ProposalId)))
        {
            var to = dep.Proposal.Target.GetValueOrDefault("toProject");
            if (to is null || !(string.Equals(to, oldName, StringComparison.OrdinalIgnoreCase) || string.Equals(Slug.From(to), oldSlug, StringComparison.OrdinalIgnoreCase))) continue;
            var target = new Dictionary<string, string>(dep.Proposal.Target, StringComparer.Ordinal) { ["toProject"] = newName };
            dep.Proposal = dep.Proposal with { Target = target };
            dep.Decision = PolicyEngine.Decide(dep.Proposal, WorldFor(task));
            if (dep.Decision.Outcome == DecisionOutcome.Deny) dep.Status = "denied";
            PersistProposal(dep.Proposal);
            Append(EventTypes.ProposalDecided, new { taskId = task.TaskId, proposalId = dep.Proposal.ProposalId, action = dep.Proposal.Action, outcome = dep.Decision.Outcome.ToString(), tier = dep.Decision.Tier.ToString(), reasons = dep.Decision.Reasons, target = dep.Decision.NormalizedTarget, retargetedFrom = oldName, retargetedTo = newName });
        }
    }

    public void ApproveAll(string? taskId = null)
    {
        var task = taskId is not null ? _tasks.FirstOrDefault(t => t.TaskId == taskId) : Foreground ?? _tasks.FirstOrDefault(t => t.Status == TaskStatus.AwaitingApproval);
        if (task is null || task.Status != TaskStatus.AwaitingApproval) { Notify(); return; }
        foreach (var ps in task.Proposals.Where(p => p.Status == "pending").ToList())
        {
            var hash = ps.Proposal.Hash();
            if (Append(EventTypes.ApprovalGranted, new { taskId = task.TaskId, proposalId = ps.Proposal.ProposalId, action = ps.Proposal.Action, proposalHash = hash, by = "user", target = ps.Decision.NormalizedTarget }) is null) { Notify(); return; }
            ps.Status = "approved";
            ps.Capability = _capabilities.Issue(ps.Proposal, _clock.UtcNow);
        }
        task.UserResponse ??= "approved_all";
        Arbiter.Resolve(task.TaskId, null, "", "", _clock.UtcNow);
        AdvanceTask(task);
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // User-initiated operations (the click is the approval; the path is still proposal → policy → capability → executor)
    // ----------------------------------------------------------------------------------------

    public bool CreateProject(string name, string? slug = null, string? parent = null)
        => RunUserOperation(Actions.CreateProject, $"Create project '{name}'", Targets(("name", name), ("slug", slug), ("parent", parent)), "Created from the Projects panel.");

    /// <summary>
    /// The New project dialog: the user picks the folder the project should live in. When that folder
    /// is not already inside a registered project folder it is registered first (recorded, never proposed),
    /// then the project itself goes through proposal → policy → executor like every other write.
    /// </summary>
    public bool CreateProjectIn(string folder, string name, string? slug = null)
    {
        if (_shutDown) return false;
        if (string.IsNullOrWhiteSpace(folder)) { _notice = "Choose the folder the project should be created in."; Notify(); return false; }
        if (!_services.Roots.Check(folder).Ok && !RegisterWorkspace(folder)) return false;
        return CreateProject(name, slug, folder);
    }

    public bool ArchiveProject(string projectId)
        => RunUserOperation(Actions.ArchiveProject, "Archive project", Targets(("projectId", projectId)), "Archived from the Projects panel.");

    public bool RestoreProject(string projectId)
        => RunUserOperation(Actions.RestoreProject, "Restore project", Targets(("projectId", projectId)), "Restored from the Projects panel.");

    public bool RenameProject(string projectId, string newName)
        => RunUserOperation(Actions.RenameProject, $"Rename project to '{newName}'", Targets(("projectId", projectId), ("newName", newName)), "Renamed from the Projects panel.");

    public bool DeleteProject(string projectId)
        => RunUserOperation(Actions.DeleteProject, "Delete project", Targets(("projectId", projectId), ("confirm", "delete")), "Deleted from the Projects panel after confirmation.");

    public bool ExportBackup(string? path = null)
        => RunUserOperation(Actions.ExportBackup, "Export backup", Targets(("path", path)), "Requested from the Diagnostics panel.");

    public bool PromoteDraftNote(string noteId, string projectId, string? type = null)
        => RunUserOperation(Actions.RouteNote, "File draft note", Targets(("noteId", noteId), ("projectId", projectId), ("type", type), ("confidence", "1")), "Filed by you from Review.");

    public bool MoveNote(string projectId, string noteId, string toProjectId)
        => RunUserOperation(Actions.MoveNote, "Move note", Targets(("projectId", projectId), ("noteId", noteId), ("toProjectId", toProjectId)), "Moved from the Projects panel.");

    /// <summary>A preference change made in the UI still goes through proposal → policy → change set, so it is recorded and revertible like any other.</summary>
    public bool UpdatePreference(string key, string value, string? projectId = null, string? noteType = null, string? action = null)
        => RunUserOperation(Actions.UpdatePreference, $"Preference {key}", Targets(("key", key), ("value", value), ("projectId", projectId), ("noteType", noteType), ("action", action)), "Changed from Settings.");

    public bool RevertChangeSet(string changeSetId)
    {
        if (SelfChange is null) { _notice = "Self-change operations are not configured."; Notify(); return false; }
        var result = SelfChange.Revert(changeSetId, "reverted by user", this);
        if (result.Status != ExecutionStatus.Completed) { _notice = result.Error; Notify(); return false; }
        _compiledPreferences = null;
        _receipt = result.Summary;
        Notify();
        return true;
    }

    private static Dictionary<string, string> Targets(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) if (!string.IsNullOrWhiteSpace(v)) d[k] = v;
        return d;
    }

    private bool RunUserOperation(string action, string title, Dictionary<string, string> target, string reason)
    {
        if (_shutDown) return false;
        if (_state is not (RelayState.Idle or RelayState.Completed) || Foreground is not null)
        {
            _notice = _state.IsTurnActive() || Foreground is not null ? TransitionTable.FinishInstructionFirst : "Return to IDLE first.";
            Notify();
            return false;
        }
        var now = _clock.UtcNow;
        var task = NewTask(TaskOrigin.Direct, action == Actions.UpdatePreference ? TaskKind.Improve : TaskKind.Organize, "user_operation", "", "", title, foreground: true, title: title);
        var proposal = new Proposal(Ulid.NewUlid(now), action, reason, target, [], [], action == Actions.ModelRequest ? Risks.External : Risks.ControlledWrite, PolicyEngine.TierOf(action) == Tier.RequiresApproval, Producers.User);
        _receiptTimer?.Dispose();
        _receipt = null;
        _lastForeground = task;
        if (Append(EventTypes.TaskCreated, new { taskId = task.TaskId, origin = task.Origin.Wire(), kind = task.Kind.Wire(), lane = task.Lane, foreground = true, action, title }) is null) return false;
        task.Plan = new TurnPlan(true, title, ["Requested directly by you", $"Check policy for {action}"], null, [], [proposal], Producers.User);
        ReceiveProposal(task, proposal);
        var ps = task.Proposals[0];
        if (ps.Status == "denied")
        {
            _notice = $"{title}: " + string.Join(" ", ps.Decision.Reasons);
            FinishTask(task);
            Notify();
            return false;
        }
        if (ps.Status == "pending")
        {
            // The user's own click is the approval; it is still recorded as one.
            Append(EventTypes.ApprovalGranted, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action, proposalHash = proposal.Hash(), by = "user", implicitViaUi = true, target = ps.Decision.NormalizedTarget });
            ps.Status = "approved";
        }
        if (_state == RelayState.Completed) Apply(Trigger.Dismiss);
        if (!Apply(Trigger.BeginExecution).Accepted) { _tasks.Remove(task); Notify(); return false; }
        task.Status = TaskStatus.Executing;
        PersistTask(task);
        RunExecutionQueue(task);
        Notify();
        return task.Outcome == "executed";
    }

    // ----------------------------------------------------------------------------------------
    // Project folders (registered roots): the only places project writes may touch. Registration is
    // a direct user act (New project… in a new folder, or a test), recorded in the ledger, never proposed.
    // ----------------------------------------------------------------------------------------

    public bool RegisterWorkspace(string path, string? label = null)
    {
        try
        {
            var root = _services.Roots.Add(path, label, _clock.UtcNow);
            Append(EventTypes.WorkspaceRegistered, new { path = root.Path, label });
            _notice = null;
            Notify();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _notice = "Project folder not registered: " + ex.Message;
            Notify();
            return false;
        }
    }

    public bool RemoveWorkspace(string path)
    {
        var removed = _services.Roots.Remove(path);
        if (removed) Append(EventTypes.WorkspaceRemoved, new { path });
        Notify();
        return removed;
    }

    // ----------------------------------------------------------------------------------------
    // Settings changed through the UI
    // ----------------------------------------------------------------------------------------

    /// <summary>Applies a settings change immediately for the orchestrator/model/listening/stream/worker sections and persists it. Hotkeys and capture timing still need a restart.</summary>
    public IReadOnlyList<string> UpdateSettings(Action<RelaySettings> mutate)
    {
        var json = JsonSerializer.Serialize(_settings, RelayJson.Indented);
        var copy = JsonSerializer.Deserialize<RelaySettings>(json, RelayJson.Indented)!;
        mutate(copy);
        var problems = SettingsStore.Save(_root, copy);
        if (problems.Count > 0)
        {
            _notice = "Settings not saved: " + string.Join(" ", problems);
            Notify();
            return problems;
        }
        _settings.Orchestrator = copy.Orchestrator;
        _settings.Model = copy.Model;
        _settings.Listening = copy.Listening;
        _settings.Stream = copy.Stream;
        _settings.ExternalModels = copy.ExternalModels;
        _settings.Workers = copy.Workers;
        _settings.Hotkeys = copy.Hotkeys;
        _settings.Capture = copy.Capture;
        Append(EventTypes.SettingsChanged, new { hash = copy.ComputeHash(), orchestratorMode = copy.Orchestrator.Mode, modelEnabled = copy.Model.Enabled, endpoint = copy.Model.Endpoint, model = copy.Model.Model, listening = copy.Listening.Enabled, externalProfiles = copy.ExternalModels.Select(p => p.Name) });
        SettingsChanged?.Invoke(copy);
        Notify();
        return problems;
    }

    public event Action<RelaySettings>? SettingsChanged;

    public RelaySettings CurrentSettings => _settings;

    /// <summary>Whether an API key is stored for the configured model secret. The key itself is never exposed.</summary>
    public bool ModelKeyStored => _services.Secrets?.Exists(_settings.Model.SecretName) == true;

    /// <summary>Stores or clears an API key in the protected secret store. Only the secret's name reaches the ledger.</summary>
    public bool SetModelApiKey(string? key, string? secretName = null)
    {
        if (_services.Secrets is null) { _notice = "This host has no protected secret store."; Notify(); return false; }
        var name = secretName ?? _settings.Model.SecretName;
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var removed = _services.Secrets.Remove(name);
                Append(EventTypes.SecretChanged, new { name, action = removed ? "removed" : "absent" });
                _notice = removed ? $"Key '{name}' removed." : $"No key was stored under '{name}'.";
            }
            else
            {
                _services.Secrets.Set(name, key.Trim());
                Append(EventTypes.SecretChanged, new { name, action = "stored", chars = key.Trim().Length });
                _notice = $"Key '{name}' stored (DPAPI, this Windows account only).";
            }
            Notify();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            _notice = "Could not update the key: " + ex.Message;
            Notify();
            return false;
        }
    }

    private SelfChangeRuntime? SelfChange => _executor.SelfChange as SelfChangeRuntime;

    // ----------------------------------------------------------------------------------------
    // Persistence for crash detection and the post-hoc record
    // ----------------------------------------------------------------------------------------

    private string LiveTaskPath(TaskState task) => Path.Combine(_root.TasksDirectory, task.TaskId + ".live.json");

    private void PersistTask(TaskState task)
    {
        try
        {
            Directory.CreateDirectory(_root.TasksDirectory);
            var payload = new
            {
                taskId = task.TaskId,
                origin = task.Origin.Wire(),
                kind = task.Kind.Wire(),
                lane = task.Lane,
                captureId = task.CaptureId,
                sourceEventId = task.SourceEventId,
                instructionChars = task.Instruction.Length,
                stage = task.Status.Wire(),
                startedAt = task.StartedAt,
                updatedAt = _clock.UtcNow,
                proposals = task.Proposals.Select(p => new { p.Proposal.ProposalId, p.Proposal.Action, p.Status }),
            };
            AtomicFile.WriteAllText(LiveTaskPath(task), JsonSerializer.Serialize(payload, RelayJson.Indented));
        }
        catch (IOException) { /* crash detection degrades; the ledger still has the truth */ }
    }

    private void WriteDiagnostics(TaskState task)
    {
        try
        {
            Directory.CreateDirectory(_root.TasksDirectory);
            var record = BuildDiagnostics(task);
            AtomicFile.WriteAllText(Path.Combine(_root.TasksDirectory, task.TaskId + ".json"), JsonSerializer.Serialize(record, RelayJson.Indented));
            var live = LiveTaskPath(task);
            if (File.Exists(live)) File.Delete(live);
        }
        catch (IOException) { }
    }

    private TaskDiagnostics BuildDiagnostics(TaskState task) => new()
    {
        TaskId = task.TaskId,
        Origin = task.Origin.Wire(),
        Kind = task.Kind.Wire(),
        Status = task.Status.Wire(),
        Outcome = task.Outcome,
        FocusedPrompt = task.Instruction,
        SourceEventId = task.SourceEventId,
        ExcerptId = task.ExcerptId,
        ParentTaskId = task.ParentTaskId,
        Planner = task.Plan?.Producer,
        Summary = task.Plan?.Summary,
        Steps = task.Plan?.Steps ?? [],
        Answer = task.Plan?.Answer,
        Citations = task.Plan?.Citations.Select(c => c.Id).ToList() ?? [],
        Knowledge = task.Plan?.Knowledge ?? KnowledgeState.Empty,
        ToolCalls = task.ToolCalls.ToList(),
        ModelCalls = task.ModelCalls.ToList(),
        Proposals = task.Proposals.Select(p => new ProposalRecord(p.Proposal.ProposalId, p.Proposal.Action, p.Decision.NormalizedTarget.Count > 0 ? p.Decision.NormalizedTarget : p.Proposal.Target,
            p.Decision.Tier.ToString(), p.Status, p.Decision.Reasons, p.Proposal.Dependencies, p.Result?.Summary, p.Result?.Error, p.GrantedBy)).ToList(),
        Presentation = task.Presentation.Wire(),
        PresentationReason = task.PresentationReason,
        UserResponse = task.UserResponse,
        PromptTokens = task.PromptTokens,
        CompletionTokens = task.CompletionTokens,
        StartedAt = task.StartedAt,
        CompletedAt = task.CompletedAt,
        WallMs = task.CompletedAt is { } done ? (long)(done - task.StartedAt).TotalMilliseconds : 0,
    };

    /// <summary>The post-hoc record of a finished task, read back from disk (what the diagnostics drawer shows).</summary>
    public TaskDiagnostics? ReadDiagnostics(string taskId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(_root.TasksDirectory, taskId + ".json"));
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<TaskDiagnostics>(text, RelayJson.Indented); } catch (JsonException) { return null; }
    }

    private void PersistProposal(Proposal proposal)
    {
        try
        {
            Directory.CreateDirectory(_root.ProposalsDirectory);
            AtomicFile.WriteAllText(Path.Combine(_root.ProposalsDirectory, proposal.ProposalId + ".json"), proposal.ToJson());
        }
        catch (IOException) { }
    }

    private void DetectInterruptedWork()
    {
        if (Directory.Exists(_root.TasksDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_root.TasksDirectory, "*.live.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string? taskId = null, stage = null, origin = null, kind = null;
                var chars = 0;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    taskId = doc.RootElement.TryGetProperty("taskId", out var t) ? t.GetString() : null;
                    stage = doc.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null;
                    origin = doc.RootElement.TryGetProperty("origin", out var o) ? o.GetString() : null;
                    kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    chars = doc.RootElement.TryGetProperty("instructionChars", out var c) ? c.GetInt32() : 0;
                }
                catch (Exception ex) when (ex is JsonException or IOException) { }
                Append(EventTypes.TaskInterruptedFound, new { taskId, stage, origin, kind, instructionChars = chars });
                if (origin == "direct")
                {
                    _recoveryReview.Add(new ReviewItem(ReviewItemKind.TurnInterrupted,
                        $"An instruction was {stage ?? "in progress"} when Relay last closed",
                        "Nothing further ran. The instruction text is preserved in the ledger; re-issue it if you still want it done.", taskId));
                }
                try { File.Move(file, Path.ChangeExtension(file, null) + ".interrupted.json", overwrite: true); } catch (IOException) { }
            }
        }
        // Older runs kept a single current-turn file.
        var turnText = AtomicFile.ReadAllTextIfExists(_root.CurrentTurnPath);
        if (turnText is not null)
        {
            Append(EventTypes.TaskInterruptedFound, new { taskId = (string?)null, stage = "unknown", origin = "direct", kind = (string?)null, instructionChars = 0, legacy = true });
            _recoveryReview.Add(new ReviewItem(ReviewItemKind.TurnInterrupted, "An instruction was in progress when Relay last closed", "Nothing further ran. Re-issue it if you still want it done.", null));
            try { File.Move(_root.CurrentTurnPath, Path.Combine(_root.TurnsDirectory, "legacy.interrupted.json"), overwrite: true); } catch (IOException) { }
        }

        foreach (var entry in Executor.FindInterrupted(_root))
        {
            Append(EventTypes.ExecutionInterruptedFound, new { proposalId = entry.ProposalId, action = entry.Action, taskId = entry.TurnId, startedAt = entry.StartedAt, target = entry.Target });
            _recoveryReview.Add(new ReviewItem(ReviewItemKind.ExecutionInterrupted,
                $"Operation '{entry.Action}' was interrupted",
                $"Started {entry.StartedAt.ToLocalTime():g} and never finished. Inspect the target before relying on it: {Describe(entry.Target)}", entry.ProposalId));
            entry.CompletedAt = _clock.UtcNow;
            entry.Status = "interrupted";
            entry.Error = "process ended before completion";
            try { AtomicFile.WriteAllText(Executor.JournalPath(_root, entry.ProposalId), JsonSerializer.Serialize(entry, RelayJson.Indented)); } catch (IOException) { }
        }

        foreach (var problem in _services.IndexProblems)
        {
            _recoveryReview.Add(new ReviewItem(ReviewItemKind.IndexProblem, "Content could not be indexed", problem));
        }

        static string Describe(IReadOnlyDictionary<string, string>? target) => target is null ? "(unknown)" : string.Join(", ", target.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private void RefreshIndexAfter(ProposalState ps)
    {
        if (ps.Result?.Status != ExecutionStatus.Completed) return;
        try
        {
            var target = ps.Decision.NormalizedTarget.Count > 0 ? ps.Decision.NormalizedTarget : ps.Proposal.Target;
            switch (ps.Proposal.Action)
            {
                case Actions.RouteNote:
                case Actions.ModifyNote:
                case Actions.SupersedeNote:
                case Actions.ApplyPatch:
                    if (_services.Registry.ById(target.GetValueOrDefault("projectId") ?? "") is { } project && Directory.Exists(project.RootPath))
                    {
                        _services.Index.RemoveProject(project.Id);
                        foreach (var (note, _) in ProjectNoteStore.ReadAll(project.RootPath).Notes) _services.Index.IndexNote(note, project);
                        if (ps.Proposal.Action == Actions.RouteNote) _services.Index.Remove(SearchIndex.DraftKind, target["noteId"]);
                    }
                    break;
                case Actions.MoveNote:
                    // The destination may have been created by a prerequisite after this proposal was decided; the executor reports where the note went.
                    foreach (var side in new[] { target.GetValueOrDefault("projectId"), target.GetValueOrDefault("toProjectId") ?? ps.Result.Outputs.GetValueOrDefault("projectId") })
                    {
                        if (_services.Registry.ById(side ?? "") is { } moved && Directory.Exists(moved.RootPath))
                        {
                            _services.Index.RemoveProject(moved.Id);
                            foreach (var (note, _) in ProjectNoteStore.ReadAll(moved.RootPath).Notes) _services.Index.IndexNote(note, moved);
                        }
                    }
                    break;
                case Actions.CreateDraftNote:
                    if (ps.Result.Outputs.TryGetValue("noteId", out var id) && _notes.Read(id) is { } draft) _services.Index.IndexDraft(draft);
                    break;
                case Actions.ArchiveProject:
                case Actions.DeleteProject:
                    _services.Index.RemoveProject(target["projectId"]);
                    break;
                case Actions.RestoreProject:
                    if (_services.Registry.ById(target["projectId"]) is { } restored && Directory.Exists(restored.RootPath))
                        foreach (var (note, _) in ProjectNoteStore.ReadAll(restored.RootPath).Notes) _services.Index.IndexNote(note, restored);
                    break;
                case Actions.UpdatePreference:
                case Actions.UpdatePrompt:
                    _compiledPreferences = null;
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException) { _notice = "Search index could not be refreshed: " + ex.Message; }
    }

    // ----------------------------------------------------------------------------------------
    // Views
    // ----------------------------------------------------------------------------------------

    private TaskView? ResponseView()
    {
        var task = Foreground ?? _lastForeground;
        return task is null ? null : ViewOf(task);
    }

    private IReadOnlyList<TaskView> TaskViews() => _tasks.Select(ViewOf).ToList();

    private TaskView ViewOf(TaskState task)
    {
        var plan = task.Plan;
        var proposals = task.Proposals.Select(p =>
        {
            var (title, detail) = ProposalText.Describe(p.Proposal, p.Decision);
            return new ProposalView(p.Proposal.ProposalId, p.Proposal.Action, title, detail, p.Decision.NormalizedTarget.Count > 0 ? p.Decision.NormalizedTarget : p.Proposal.Target,
                p.Decision.Tier, p.Status, p.Decision.Reasons, p.Result?.Summary, p.Result?.Error, p.Status == "pending" && ProposalText.EditableKeys(p.Proposal.Action).Count > 0, p.Proposal.ProposedBy, p.Proposal.Reason,
                p.Proposal.Dependencies, p.GrantedBy, p.Status == "pending" && DependenciesDead(task, p) ? BlockedReason(task, p) : null);
        }).ToList();
        var externalPending = task.PendingOperation?.Proposal.Action == Actions.ModelRequest;
        return new TaskView(task.TaskId, task.Origin, task.Kind, task.Status, TaskLanes.Tag(task.Kind, task.Status, externalPending), task.Lane, task.Instruction,
            plan?.Summary ?? (task.Status == TaskStatus.Planning ? "Planning…" : ""), plan?.Steps ?? [], plan?.Answer, plan?.Citations ?? [], proposals,
            plan?.Producer ?? PlannerName, task.Outcome, task.StartedAt, task.CompletedAt, task.IsLive, task.Foreground, task.ExcerptId, task.Title, task.Why, task.Confidence,
            plan?.Knowledge ?? KnowledgeState.Empty, plan?.Consistent, task.Presentation, task.PresentationReason,
            new TaskCost(task.PromptTokens, task.CompletionTokens, task.ModelCalls.Count, task.ToolCalls.Count, (long)((task.CompletedAt ?? _clock.UtcNow) - task.StartedAt).TotalMilliseconds),
            task.ToolCalls.ToList(), task.ModelCalls.ToList(), task.UserResponse, task.ParentTaskId);
    }

    private IReadOnlyList<ProjectView> ProjectViews()
        => _services.Registry.All.Select(p => new ProjectView(p.Id, p.Slug, p.Name, p.Status, p.RootPath, p.IsActive ? Directory.Exists(p.RootPath) : p.ArchivedPath is not null && Directory.Exists(p.ArchivedPath))).ToList();

    private IReadOnlyList<WorkspaceView> WorkspaceViews()
        => _services.Roots.Registered.Select(r => new WorkspaceView(r.Path, r.Label, Directory.Exists(r.Path))).ToList();

    private IReadOnlyList<ChangeSetView> ChangeSetViews()
    {
        try
        {
            return ChangeSets.All().OrderByDescending(c => c.AppliedAt).Take(20)
                .Select(c => new ChangeSetView(c.ChangeSetId, c.Kind, Path.GetFileName(c.Path), c.Reason, c.AppliedAt, c.RevertedAt, c.TaskId)).ToList();
        }
        catch (IOException) { return []; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Marshals planner progress onto the coordinator thread, into the ledger, and into the task's diagnostics while the task is live.</summary>
    private sealed class TaskSink : ITurnSink
    {
        private readonly SessionCoordinator _owner;
        private readonly TaskState _task;

        public TaskSink(SessionCoordinator owner, TaskState task) { _owner = owner; _task = task; }

        private void Record(string type, object data, Action? also = null) => _owner._scheduler.Post(() =>
        {
            if (!_task.IsLive) return;
            also?.Invoke();
            _owner.Append(type, data);
            _owner.Notify();
        });

        // Tool arguments and summaries of an overheard task quote the room (a search for the words just heard); the ledger gets fingerprints, the task record the text.
        public void Progress(string text) => Record(EventTypes.TurnProgress, new { taskId = _task.TaskId, text = Guarded(_task, text) });
        public void ToolCalled(string tool, IReadOnlyDictionary<string, string> args) => Record(EventTypes.ToolCalled, new { taskId = _task.TaskId, tool, args = Guarded(_task, args) },
            () => _task.ToolCalls.Add(new ToolCallRecord(tool, new Dictionary<string, string>(args, StringComparer.Ordinal), false, "…", 0, _owner._clock.UtcNow)));
        public void ToolReturned(string tool, bool ok, string summary, int items) => Record(EventTypes.ToolReturned, new { taskId = _task.TaskId, tool, ok, summary = Guarded(_task, summary), items },
            () =>
            {
                var i = _task.ToolCalls.FindLastIndex(c => c.Tool == tool && c.Summary == "…");
                if (i >= 0) _task.ToolCalls[i] = _task.ToolCalls[i] with { Ok = ok, Summary = summary, Items = items };
                else _task.ToolCalls.Add(new ToolCallRecord(tool, new Dictionary<string, string>(), ok, summary, items, _owner._clock.UtcNow));
            });
        public void ModelRequested(string host, string model, int promptChars, int sources) => Record(EventTypes.ModelRequested, new { taskId = _task.TaskId, host, model, promptChars, sources },
            () => _task.ModelCalls.Add(new ModelCallRecord(host, model, promptChars, 0, 0, 0, false, null, _owner._clock.UtcNow)));
        public void ModelResponded(bool ok, int chars, long elapsedMs, string? error, int promptTokens = 0, int completionTokens = 0) => Record(EventTypes.ModelResponded, new { taskId = _task.TaskId, ok, chars, elapsedMs, error, promptTokens, completionTokens },
            () =>
            {
                var i = _task.ModelCalls.FindLastIndex(m => m.ElapsedMs == 0 && !m.Ok && m.Error is null);
                if (i >= 0) _task.ModelCalls[i] = _task.ModelCalls[i] with { PromptTokens = promptTokens, CompletionTokens = completionTokens, ElapsedMs = elapsedMs, Ok = ok, Error = error };
                else _task.ModelCalls.Add(new ModelCallRecord("", "", 0, promptTokens, completionTokens, elapsedMs, ok, error, _owner._clock.UtcNow));
            });
    }
}
