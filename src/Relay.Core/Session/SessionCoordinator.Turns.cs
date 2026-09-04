using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Workspaces;

namespace Relay.Core.Session;

/// <summary>Dependencies added for the orchestrator phases; bundled so the coordinator's constructor stays readable.</summary>
public sealed class CoordinatorServices
{
    public required ProjectRegistry Registry { get; init; }
    public required WorkspaceRoots Roots { get; init; }
    public required IOrchestrator Orchestrator { get; init; }
    public required SearchIndex Index { get; init; }
    public IWorkerOperations? Workers { get; init; }
    public IReadOnlyList<string> IndexProblems { get; init; } = [];
}

/// <summary>
/// The command turn: PLANNING → (AWAITING_APPROVAL) → (EXECUTING) → COMPLETED, plus user-initiated
/// operations that reuse the same proposal → policy → capability → executor path so there is exactly
/// one way anything canonical changes.
/// </summary>
public sealed partial class SessionCoordinator : IExecutionSink
{
    private sealed class ProposalState
    {
        public required Proposal Proposal { get; set; }
        public required Decision Decision { get; set; }
        public required string Status { get; set; }   // pending | approved | rejected | denied | allowed | executing | executed | failed | skipped | edited
        public Capability? Capability { get; set; }
        public ExecutionResult? Result { get; set; }
    }

    private sealed class TurnState
    {
        public required string TurnId { get; init; }
        public required string Kind { get; init; }     // command | user_operation
        public required string CaptureId { get; init; }
        public required string SourceEventId { get; init; }
        public required string Instruction { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public TurnPlan? Plan { get; set; }
        public List<ProposalState> Proposals { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public IDisposable? Timeout { get; set; }
        public bool StopRequested { get; set; }
        public ProposalState? PendingOperation { get; set; }
        public string? Outcome { get; set; }
    }

    private TurnState? _turn;
    private TurnState? _lastTurn;
    private readonly List<ReviewItem> _recoveryReview = new();

    public string OrchestratorMode => _settings.Orchestrator.Mode;
    public bool OrchestratorEnabled => _settings.Orchestrator.Mode != OrchestratorSettings.Off;

    LedgerRecord? IExecutionSink.Record(string type, object data) => Append(type, data);

    private PolicyWorld World => new()
    {
        Registry = _services.Registry,
        Roots = _services.Roots,
        DataRoot = _root,
        DraftNoteExists = id => _notes.Read(id) is not null,
        ProjectNoteExists = (projectId, noteId) => _services.Registry.ById(projectId) is { } p && Directory.Exists(p.RootPath) && ProjectNoteStore.Find(p.RootPath, noteId) is not null,
        WorkersEnabled = _settings.Workers.Enabled && _services.Workers is not null,
        AgentRunHasOutput = _services.Workers is null ? null : runId => Directory.Exists(Path.Combine(_root.AgentsDirectory, runId, "out")),
    };

    // ----------------------------------------------------------------------------------------
    // Command turns
    // ----------------------------------------------------------------------------------------

    private void StartCommandTurn(Captures.CaptureDraft draft, string sourceEventId)
    {
        var now = _clock.UtcNow;
        var turn = new TurnState
        {
            TurnId = Ulid.NewUlid(now),
            Kind = "command",
            CaptureId = draft.CaptureId,
            SourceEventId = sourceEventId,
            Instruction = draft.Text,
            StartedAt = now,
        };
        _turn = turn;
        _lastTurn = turn;
        if (Append(EventTypes.TurnStarted, new { turnId = turn.TurnId, kind = turn.Kind, captureId = draft.CaptureId, sourceEventId, chars = draft.Text.Length, orchestrator = _services.Orchestrator.Name }) is null) return;
        PersistTurn(turn, "planning");
        if (!Apply(Trigger.BeginPlanning).Accepted) return;

        turn.Timeout = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Orchestrator.PlanningTimeoutMs), () =>
        {
            if (_turn != turn || _state != RelayState.Planning) return;
            FailTurn(turn, "planning_timeout", $"The orchestrator did not answer within {_settings.Orchestrator.PlanningTimeoutMs / 1000}s.", null);
            turn.Cts.Cancel(); // after the turn is detached, so the cancelled task's continuation finds nothing to do
            Notify();
        });

        var request = new TurnRequest(turn.TurnId, draft.CaptureId, sourceEventId, draft.Text, now);
        var sink = new TurnSink(this, turn.TurnId);
        var context = new TurnContext
        {
            Tools = new ToolBroker(_services.Registry, _notes, _services.Index, sink, _settings.Orchestrator.MaxToolCalls),
            Sink = sink,
            Registry = _services.Registry,
            Roots = _services.Roots,
            Drafts = _notes,
            Settings = _settings.Orchestrator,
            CompletedRuns = projectId => Agents.AgentRunStatus.All(_root).Where(s => s.ProjectId == projectId && s.State == "completed" && !s.Applied).ToList(),
        };

        Task<TurnPlan> task;
        try { task = _services.Orchestrator.PlanAsync(request, context, turn.Cts.Token); }
        catch (Exception ex) { task = Task.FromException<TurnPlan>(ex); }
        task.ContinueWith(t => _scheduler.Post(() => OnPlanFinished(turn, t)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnPlanFinished(TurnState turn, Task<TurnPlan> task)
    {
        if (_turn != turn || _state != RelayState.Planning) return; // cancelled, timed out, or failed meanwhile
        turn.Timeout?.Dispose();
        turn.Timeout = null;

        if (task.IsCanceled) return;
        if (task.IsFaulted)
        {
            var ex = task.Exception?.GetBaseException() ?? new InvalidOperationException("Planning failed.");
            FailTurn(turn, "planning_failed", ex.Message, ex.ToString());
            Notify();
            return;
        }

        var plan = task.Result;
        turn.Plan = plan;
        if (Append(EventTypes.PlanProposed, new
        {
            turnId = turn.TurnId,
            producer = plan.Producer,
            understood = plan.Understood,
            summary = plan.Summary,
            steps = plan.Steps,
            answer = plan.Answer,
            citations = plan.Citations.Select(c => new { c.Kind, c.Id, c.ProjectSlug, span = c.Span }),
            proposals = plan.Proposals.Count,
            raw = plan.Raw,
        }) is null) return;

        if (!plan.Understood)
        {
            turn.Plan = plan with
            {
                Answer = _settings.Model.Enabled
                    ? "The instruction could not be interpreted."
                    : "The built-in grammar did not understand that instruction, and the model gateway is not enabled. Try: \"create project <name>\", \"archive project <name>\", \"list projects\", \"remember that …\", \"file the last note under <project>\", \"what did I say about …\", \"summarize project <name>\", \"export a backup\".",
            };
        }

        foreach (var proposal in plan.Proposals) ReceiveProposal(turn, proposal);
        AdvanceTurn(turn);
        Notify();
    }

    private void ReceiveProposal(TurnState turn, Proposal proposal)
    {
        PersistProposal(proposal);
        Append(EventTypes.ProposalReceived, new { turnId = turn.TurnId, proposalId = proposal.ProposalId, action = proposal.Action, reason = proposal.Reason, target = proposal.Target, sourceEventIds = proposal.SourceEventIds, expectedEffects = proposal.ExpectedEffects, risk = proposal.Risk, requiresApproval = proposal.RequiresApproval, proposedBy = proposal.ProposedBy, hash = proposal.Hash() });
        var decision = PolicyEngine.Decide(proposal, World);
        Append(EventTypes.ProposalDecided, new { turnId = turn.TurnId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = decision.Outcome.ToString(), tier = decision.Tier.ToString(), reasons = decision.Reasons, target = decision.NormalizedTarget });
        var status = decision.Outcome switch
        {
            DecisionOutcome.Allow => "allowed",
            DecisionOutcome.NeedsApproval => "pending",
            _ => "denied",
        };
        turn.Proposals.Add(new ProposalState { Proposal = proposal, Decision = decision, Status = status });
    }

    /// <summary>Moves the turn to approval, execution, or completion depending on what the proposals need.</summary>
    private void AdvanceTurn(TurnState turn)
    {
        if (turn.Proposals.Any(p => p.Status == "pending"))
        {
            PersistTurn(turn, "awaiting_approval");
            if (_state == RelayState.Planning) Apply(Trigger.ApprovalRequired);
            return;
        }
        if (turn.Proposals.Any(p => p.Status is "allowed" or "approved"))
        {
            PersistTurn(turn, "executing");
            if (_state is RelayState.Planning or RelayState.AwaitingApproval) Apply(Trigger.BeginExecution);
            RunExecutionQueue(turn);
            return;
        }
        FinishTurn(turn);
    }

    private void RunExecutionQueue(TurnState turn)
    {
        while (true)
        {
            var next = turn.Proposals.FirstOrDefault(p => p.Status is "allowed" or "approved");
            if (next is null) break;
            if (turn.StopRequested)
            {
                foreach (var p in turn.Proposals.Where(p => p.Status is "allowed" or "approved")) p.Status = "skipped";
                break;
            }
            next.Status = "executing";
            next.Capability ??= _capabilities.Issue(next.Proposal, _clock.UtcNow);
            var result = _executor.Execute(next.Proposal, next.Capability, World, turn.TurnId, this);
            next.Result = result;
            if (result.Status == ExecutionStatus.Pending)
            {
                turn.PendingOperation = next;
                Notify();
                return; // resumed by CompletePendingOperation
            }
            next.Status = result.Status == ExecutionStatus.Completed ? "executed" : "failed";
            RefreshIndexAfter(next);
            Notify();
        }
        FinishTurn(turn);
    }

    /// <summary>Called (via Post) when an asynchronous operation such as a worker run has finished.</summary>
    public void CompletePendingOperation(string proposalId, ExecutionResult result)
    {
        var turn = _turn;
        if (turn?.PendingOperation is null || turn.PendingOperation.Proposal.ProposalId != proposalId) return;
        var op = turn.PendingOperation;
        turn.PendingOperation = null;
        _executor.Complete(op.Proposal, turn.TurnId, result, this);
        op.Result = result;
        // A worker killed because the user asked for a stop is not a failure of the turn; it is the stop working.
        op.Status = result.Status == ExecutionStatus.Completed ? "executed" : turn.StopRequested ? "stopped" : "failed";
        if (op.Status == "executed") RefreshIndexAfter(op);
        if (_state == RelayState.Executing) RunExecutionQueue(turn);
        Notify();
    }

    private void FinishTurn(TurnState turn)
    {
        turn.Timeout?.Dispose();
        var executed = turn.Proposals.Count(p => p.Status == "executed");
        var failed = turn.Proposals.Count(p => p.Status == "failed");
        var denied = turn.Proposals.Count(p => p.Status == "denied");
        var rejected = turn.Proposals.Count(p => p.Status is "rejected" or "edited");
        var skipped = turn.Proposals.Count(p => p.Status == "skipped");
        var stopped = turn.Proposals.Count(p => p.Status == "stopped");
        turn.Outcome = failed > 0 ? "failed" : stopped > 0 ? "stopped" : executed > 0 ? "executed" : denied > 0 && turn.Proposals.Count == denied ? "denied" : rejected > 0 && executed == 0 ? "rejected" : turn.Plan?.Answer is not null ? "answered" : "completed";

        Append(EventTypes.TurnCompleted, new { turnId = turn.TurnId, kind = turn.Kind, outcome = turn.Outcome, proposals = turn.Proposals.Count, executed, failed, denied, rejected, skipped, stopped, stopRequested = turn.StopRequested });
        ClearTurnFile(turn);
        _turn = null;

        if (failed > 0)
        {
            var firstError = turn.Proposals.First(p => p.Status == "failed");
            _incident = new IncidentInfo("execution_failed", $"{firstError.Proposal.Action} failed", firstError.Result?.Error ?? "unknown", _clock.UtcNow, null);
            _retryable = false;
            Apply(_state == RelayState.Executing ? Trigger.ExecutionFailed : Trigger.PlanFailed);
            return;
        }

        var receipt = turn.Outcome switch
        {
            "executed" => $"Done · {executed} operation(s) executed" + (skipped > 0 ? $", {skipped} skipped after stop" : ""),
            "stopped" => $"Stopped · worker terminated at your request, partial output kept in staging" + (executed > 0 ? $"; {executed} earlier operation(s) stand" : ""),
            "denied" => "Nothing ran · every proposal was denied by policy",
            "rejected" => "Nothing ran · proposals rejected",
            "answered" => "Answered · no changes made",
            _ => "Instruction handled · no changes made",
        };
        switch (_state)
        {
            case RelayState.Executing: Apply(Trigger.ExecutionSucceeded); break;
            case RelayState.AwaitingApproval: Apply(Trigger.AllRejected); break;
            case RelayState.Planning: Apply(Trigger.PlanReady); break;
        }
        ShowReceipt(receipt);
    }

    private void FailTurn(TurnState turn, string kind, string summary, string? detail)
    {
        turn.Timeout?.Dispose();
        turn.Outcome = "failed";
        Append(EventTypes.TurnFailed, new { turnId = turn.TurnId, kind, error = summary });
        ClearTurnFile(turn);
        _turn = null;
        _incident = new IncidentInfo(kind, summary, detail ?? summary, _clock.UtcNow, null);
        _retryable = false;
        Apply(_state == RelayState.Executing ? Trigger.ExecutionFailed : Trigger.PlanFailed);
    }

    private void CancelTurn()
    {
        var turn = _turn;
        if (turn is null) { Apply(Trigger.Cancel); return; }
        switch (_state)
        {
            case RelayState.Planning:
                turn.Timeout?.Dispose();
                Append(EventTypes.TurnCancelled, new { turnId = turn.TurnId, stage = "planning" });
                turn.Outcome = "cancelled";
                ClearTurnFile(turn);
                _turn = null;
                turn.Cts.Cancel();
                Apply(Trigger.Cancel);
                break;
            case RelayState.AwaitingApproval:
                foreach (var p in turn.Proposals.Where(p => p.Status == "pending"))
                {
                    p.Status = "rejected";
                    Append(EventTypes.ApprovalRejected, new { turnId = turn.TurnId, proposalId = p.Proposal.ProposalId, action = p.Proposal.Action, by = "user", reason = "instruction cancelled" });
                }
                Append(EventTypes.TurnCancelled, new { turnId = turn.TurnId, stage = "awaiting_approval" });
                turn.Outcome = "cancelled";
                ClearTurnFile(turn);
                _turn = null;
                Apply(Trigger.Cancel);
                break;
            case RelayState.Executing:
                if (!turn.StopRequested)
                {
                    turn.StopRequested = true;
                    Append(EventTypes.ExecutionStopRequested, new { turnId = turn.TurnId, pending = turn.PendingOperation?.Proposal.ProposalId });
                    RequestWorkerStop?.Invoke(turn.PendingOperation?.Proposal.ProposalId);
                }
                Apply(Trigger.Cancel);
                break;
        }
    }

    /// <summary>Set by the worker runtime so a stop request can terminate a running worker.</summary>
    public Action<string?>? RequestWorkerStop { get; set; }

    // ----------------------------------------------------------------------------------------
    // Approvals
    // ----------------------------------------------------------------------------------------

    public void Approve(string proposalId)
    {
        var turn = _turn;
        var ps = turn?.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == proposalId);
        if (turn is null || ps is null || _state != RelayState.AwaitingApproval || ps.Status != "pending") { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        var hash = ps.Proposal.Hash();
        if (Append(EventTypes.ApprovalGranted, new { turnId = turn.TurnId, proposalId, action = ps.Proposal.Action, proposalHash = hash, by = "user", target = ps.Decision.NormalizedTarget }) is null) { Notify(); return; }
        ps.Status = "approved";
        ps.Capability = _capabilities.Issue(ps.Proposal, _clock.UtcNow);
        AdvanceTurn(turn);
        Notify();
    }

    public void Reject(string proposalId, string? reason = null)
    {
        var turn = _turn;
        var ps = turn?.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == proposalId);
        if (turn is null || ps is null || _state != RelayState.AwaitingApproval || ps.Status != "pending") { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        if (Append(EventTypes.ApprovalRejected, new { turnId = turn.TurnId, proposalId, action = ps.Proposal.Action, by = "user", reason = reason ?? "rejected" }) is null) { Notify(); return; }
        ps.Status = "rejected";
        AdvanceTurn(turn);
        Notify();
    }

    /// <summary>Edit-and-re-propose: the edited target becomes a new proposal (attributed to the user) that is decided afresh; the original is recorded as edited.</summary>
    public void EditProposal(string proposalId, IReadOnlyDictionary<string, string> newTarget)
    {
        var turn = _turn;
        var ps = turn?.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == proposalId);
        if (turn is null || ps is null || _state != RelayState.AwaitingApproval || ps.Status != "pending") { _notice = "That proposal is not awaiting approval."; Notify(); return; }
        var replacement = ps.Proposal.WithTarget(Ulid.NewUlid(_clock.UtcNow), new Dictionary<string, string>(newTarget, StringComparer.Ordinal));
        if (Append(EventTypes.ProposalEdited, new { turnId = turn.TurnId, fromProposalId = proposalId, toProposalId = replacement.ProposalId, action = replacement.Action, oldTarget = ps.Proposal.Target, newTarget = replacement.Target }) is null) { Notify(); return; }
        ps.Status = "edited";
        ReceiveProposal(turn, replacement);
        AdvanceTurn(turn);
        Notify();
    }

    public void ApproveAll()
    {
        var turn = _turn;
        if (turn is null || _state != RelayState.AwaitingApproval) { Notify(); return; }
        foreach (var ps in turn.Proposals.Where(p => p.Status == "pending").ToList())
        {
            var hash = ps.Proposal.Hash();
            if (Append(EventTypes.ApprovalGranted, new { turnId = turn.TurnId, proposalId = ps.Proposal.ProposalId, action = ps.Proposal.Action, proposalHash = hash, by = "user", target = ps.Decision.NormalizedTarget }) is null) { Notify(); return; }
            ps.Status = "approved";
            ps.Capability = _capabilities.Issue(ps.Proposal, _clock.UtcNow);
        }
        AdvanceTurn(turn);
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // User-initiated operations (the click is the approval; the path is still proposal → policy → capability → executor)
    // ----------------------------------------------------------------------------------------

    public bool CreateProject(string name, string? slug = null, string? parent = null)
        => RunUserOperation(Actions.CreateProject, $"Create project '{name}'", Targets(("name", name), ("slug", slug), ("parent", parent)), "Created from the Projects panel.");

    public bool ArchiveProject(string projectId)
        => RunUserOperation(Actions.ArchiveProject, "Archive project", Targets(("projectId", projectId)), "Archived from the Projects panel.");

    public bool RestoreProject(string projectId)
        => RunUserOperation(Actions.RestoreProject, "Restore project", Targets(("projectId", projectId)), "Restored from the Projects panel.");

    public bool RenameProject(string projectId, string newName)
        => RunUserOperation(Actions.RenameProject, $"Rename project to '{newName}'", Targets(("projectId", projectId), ("newName", newName)), "Renamed from the Projects panel.");

    public bool ExportBackup(string? path = null)
        => RunUserOperation(Actions.ExportBackup, "Export backup", Targets(("path", path)), "Requested from the Diagnostics panel.");

    public bool PromoteDraftNote(string noteId, string projectId, string? type = null)
        => RunUserOperation(Actions.RouteNote, "File draft note", Targets(("noteId", noteId), ("projectId", projectId), ("type", type), ("confidence", "1")), "Filed by you from Review.");

    private static Dictionary<string, string> Targets(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) if (!string.IsNullOrWhiteSpace(v)) d[k] = v;
        return d;
    }

    private bool RunUserOperation(string action, string title, Dictionary<string, string> target, string reason)
    {
        if (_shutDown) return false;
        if (_state is not (RelayState.Idle or RelayState.Completed))
        {
            _notice = _state.IsTurnActive() ? TransitionTable.FinishInstructionFirst : "Return to IDLE first.";
            Notify();
            return false;
        }
        var now = _clock.UtcNow;
        var turn = new TurnState { TurnId = Ulid.NewUlid(now), Kind = "user_operation", CaptureId = "", SourceEventId = "", Instruction = title, StartedAt = now };
        var proposal = new Proposal(Ulid.NewUlid(now), action, reason, target, [], [], Risks.ControlledWrite, PolicyEngine.TierOf(action) == Tier.RequiresApproval, Producers.User);
        _receiptTimer?.Dispose();
        _receipt = null;
        _turn = turn;
        _lastTurn = turn;
        if (Append(EventTypes.TurnStarted, new { turnId = turn.TurnId, kind = turn.Kind, action, title }) is null) return false;
        turn.Plan = new TurnPlan(true, title, ["Requested directly by you", $"Check policy for {action}"], null, [], [proposal], Producers.User);
        ReceiveProposal(turn, proposal);
        var ps = turn.Proposals[0];
        if (ps.Status == "denied")
        {
            _notice = $"{title}: " + string.Join(" ", ps.Decision.Reasons);
            FinishTurn(turn);
            Notify();
            return false;
        }
        if (ps.Status == "pending")
        {
            // The user's own click is the approval; it is still recorded as one.
            Append(EventTypes.ApprovalGranted, new { turnId = turn.TurnId, proposalId = proposal.ProposalId, action, proposalHash = proposal.Hash(), by = "user", implicitViaUi = true, target = ps.Decision.NormalizedTarget });
            ps.Status = "approved";
        }
        if (_state == RelayState.Completed) Apply(Trigger.Dismiss);
        if (!Apply(Trigger.BeginExecution).Accepted) { _turn = null; Notify(); return false; }
        PersistTurn(turn, "executing");
        RunExecutionQueue(turn);
        Notify();
        return turn.Outcome == "executed";
    }

    // ----------------------------------------------------------------------------------------
    // Workspaces (UI-only; never proposed)
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
            _notice = "Workspace not registered: " + ex.Message;
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

    /// <summary>Applies a settings change immediately for the orchestrator/model/worker sections and persists it. Hotkeys and capture timing still need a restart.</summary>
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
        _settings.Workers = copy.Workers;
        _settings.Hotkeys = copy.Hotkeys;
        _settings.FlowRelay = copy.FlowRelay;
        _settings.Capture = copy.Capture;
        Append(EventTypes.SettingsChanged, new { hash = copy.ComputeHash(), orchestratorMode = copy.Orchestrator.Mode, modelEnabled = copy.Model.Enabled, endpoint = copy.Model.Endpoint, model = copy.Model.Model });
        SettingsChanged?.Invoke(copy);
        Notify();
        return problems;
    }

    public event Action<RelaySettings>? SettingsChanged;

    public RelaySettings CurrentSettings => _settings;

    // ----------------------------------------------------------------------------------------
    // Persistence for crash detection
    // ----------------------------------------------------------------------------------------

    private void PersistTurn(TurnState turn, string stage)
    {
        try
        {
            Directory.CreateDirectory(_root.TurnsDirectory);
            var payload = new
            {
                turnId = turn.TurnId,
                kind = turn.Kind,
                captureId = turn.CaptureId,
                sourceEventId = turn.SourceEventId,
                instructionChars = turn.Instruction.Length,
                stage,
                startedAt = turn.StartedAt,
                updatedAt = _clock.UtcNow,
                proposals = turn.Proposals.Select(p => new { p.Proposal.ProposalId, p.Proposal.Action, p.Status }),
            };
            AtomicFile.WriteAllText(_root.CurrentTurnPath, JsonSerializer.Serialize(payload, RelayJson.Indented));
        }
        catch (IOException) { /* crash detection degrades; the ledger still has the truth */ }
    }

    private void ClearTurnFile(TurnState turn)
    {
        try
        {
            if (File.Exists(_root.CurrentTurnPath)) File.Move(_root.CurrentTurnPath, Path.Combine(_root.TurnsDirectory, turn.TurnId + ".json"), overwrite: true);
        }
        catch (IOException) { }
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
        var turnText = AtomicFile.ReadAllTextIfExists(_root.CurrentTurnPath);
        if (turnText is not null)
        {
            string? turnId = null, stage = null;
            int chars = 0;
            try
            {
                using var doc = JsonDocument.Parse(turnText);
                turnId = doc.RootElement.TryGetProperty("turnId", out var t) ? t.GetString() : null;
                stage = doc.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null;
                chars = doc.RootElement.TryGetProperty("instructionChars", out var c) ? c.GetInt32() : 0;
            }
            catch (JsonException) { }
            Append(EventTypes.TurnInterruptedFound, new { turnId, stage, instructionChars = chars });
            _recoveryReview.Add(new ReviewItem(ReviewItemKind.TurnInterrupted,
                $"An instruction was {stage ?? "in progress"} when Relay last closed",
                "Nothing further ran. The instruction text is preserved in the ledger (capture.committed); re-issue it if you still want it done.", turnId));
            try { File.Move(_root.CurrentTurnPath, Path.Combine(_root.TurnsDirectory, (turnId ?? "unknown") + ".interrupted.json"), overwrite: true); } catch (IOException) { }
        }

        foreach (var entry in Executor.FindInterrupted(_root))
        {
            Append(EventTypes.ExecutionInterruptedFound, new { proposalId = entry.ProposalId, action = entry.Action, turnId = entry.TurnId, startedAt = entry.StartedAt, target = entry.Target });
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
            switch (ps.Proposal.Action)
            {
                case Actions.RouteNote:
                case Actions.ModifyNote:
                case Actions.SupersedeNote:
                case Actions.ApplyPatch:
                    if (_services.Registry.ById(ps.Decision.NormalizedTarget.GetValueOrDefault("projectId") ?? "") is { } project && Directory.Exists(project.RootPath))
                    {
                        _services.Index.RemoveProject(project.Id);
                        foreach (var (note, _) in ProjectNoteStore.ReadAll(project.RootPath).Notes) _services.Index.IndexNote(note, project);
                        if (ps.Proposal.Action == Actions.RouteNote) _services.Index.Remove(SearchIndex.DraftKind, ps.Decision.NormalizedTarget["noteId"]);
                    }
                    break;
                case Actions.CreateDraftNote:
                    if (ps.Result.Outputs.TryGetValue("noteId", out var id) && _notes.Read(id) is { } draft) _services.Index.IndexDraft(draft);
                    break;
                case Actions.ArchiveProject:
                    _services.Index.RemoveProject(ps.Decision.NormalizedTarget["projectId"]);
                    break;
                case Actions.RestoreProject:
                    if (_services.Registry.ById(ps.Decision.NormalizedTarget["projectId"]) is { } restored && Directory.Exists(restored.RootPath))
                        foreach (var (note, _) in ProjectNoteStore.ReadAll(restored.RootPath).Notes) _services.Index.IndexNote(note, restored);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException) { _notice = "Search index could not be refreshed: " + ex.Message; }
    }

    // ----------------------------------------------------------------------------------------
    // Views
    // ----------------------------------------------------------------------------------------

    private TurnResponse? ResponseView()
    {
        var turn = _turn ?? _lastTurn;
        if (turn is null) return null;
        var plan = turn.Plan;
        var proposals = turn.Proposals.Select(p =>
        {
            var (title, detail) = ProposalText.Describe(p.Proposal, p.Decision);
            return new ProposalView(p.Proposal.ProposalId, p.Proposal.Action, title, detail, p.Decision.NormalizedTarget.Count > 0 ? p.Decision.NormalizedTarget : p.Proposal.Target,
                p.Decision.Tier, p.Status, p.Decision.Reasons, p.Result?.Summary, p.Result?.Error, p.Status == "pending" && ProposalText.EditableKeys(p.Proposal.Action).Count > 0, p.Proposal.ProposedBy, p.Proposal.Reason);
        }).ToList();
        return new TurnResponse(turn.TurnId, turn.Kind, turn.Instruction, plan?.Summary ?? (_state == RelayState.Planning ? "Planning…" : ""), plan?.Steps ?? [], plan?.Answer, plan?.Citations ?? [], proposals, plan?.Producer ?? _services.Orchestrator.Name, turn.Outcome, turn.StartedAt, _turn == turn);
    }

    private IReadOnlyList<ProjectView> ProjectViews()
        => _services.Registry.All.Select(p => new ProjectView(p.Id, p.Slug, p.Name, p.Status, p.RootPath, p.IsActive ? Directory.Exists(p.RootPath) : p.ArchivedPath is not null && Directory.Exists(p.ArchivedPath))).ToList();

    private IReadOnlyList<WorkspaceView> WorkspaceViews()
        => _services.Roots.Registered.Select(r => new WorkspaceView(r.Path, r.Label, Directory.Exists(r.Path))).ToList();

    private IReadOnlyList<DraftNoteView> DraftViews()
        => _notes.Unrouted().Select(d => new DraftNoteView(d.NoteId, d.Type, d.CreatedAt, d.Text)).ToList();

    /// <summary>Marshals orchestrator progress onto the coordinator thread and into the ledger while the turn is live.</summary>
    private sealed class TurnSink : ITurnSink
    {
        private readonly SessionCoordinator _owner;
        private readonly string _turnId;

        public TurnSink(SessionCoordinator owner, string turnId) { _owner = owner; _turnId = turnId; }

        private void Record(string type, object data) => _owner._scheduler.Post(() =>
        {
            if (_owner._turn?.TurnId != _turnId) return;
            _owner.Append(type, data);
            _owner.Notify();
        });

        public void Progress(string text) => Record(EventTypes.TurnProgress, new { turnId = _turnId, text });
        public void ToolCalled(string tool, IReadOnlyDictionary<string, string> args) => Record(EventTypes.ToolCalled, new { turnId = _turnId, tool, args });
        public void ToolReturned(string tool, bool ok, string summary, int items) => Record(EventTypes.ToolReturned, new { turnId = _turnId, tool, ok, summary, items });
        public void ModelRequested(string host, string model, int promptChars, int sources) => Record(EventTypes.ModelRequested, new { turnId = _turnId, host, model, promptChars, sources });
        public void ModelResponded(bool ok, int chars, long elapsedMs, string? error) => Record(EventTypes.ModelResponded, new { turnId = _turnId, ok, chars, elapsedMs, error });
    }
}
