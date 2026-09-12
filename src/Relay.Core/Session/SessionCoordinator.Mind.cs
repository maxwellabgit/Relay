using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Execution;
using Relay.Core.External;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using Relay.Core.Usage;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Core.Session;

/// <summary>
/// Mind mode (docs/09, slice 1): every task runs a <see cref="TaskLoop"/> and this partial is its host —
/// the owner of consequences. Tools run through the same read-only broker, proposals through the same
/// policy engine, approvals through the same buttons, operations through the same executor and pending
/// completions; what changes is that each consequence is handed back to the loop as an observation, and
/// the task ends when the mind says it does (or a budget does). The old pipeline stays intact beside it,
/// selected by <c>orchestrator.mode</c>; nothing here runs unless the mode is "mind".
/// </summary>
public sealed partial class SessionCoordinator
{
    private bool MindMode => _settings.Orchestrator.Mode == OrchestratorSettings.Mind && _services.Mind is not null;

    private string PlannerName => MindMode ? _services.Mind!.Name : _services.Orchestrator.Name;

    private static string LoopOrigin(TaskState task) => task.Origin switch
    {
        TaskOrigin.Observed => InputObserved.Heard,
        TaskOrigin.Dialogue => InputObserved.FollowUp,
        _ => InputObserved.Ask,
    };

    // ----------------------------------------------------------------------------------------
    // Starting, driving and ending the loop
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// What every mind prompt shares, whether the mind is working a task or listening: the tools, the delegates, the
    /// user's projects and style, the approved prompt fragment, and the constitution if the user has replaced it.
    /// </summary>
    private MindContext MindContextOf(IReadOnlyList<ActionDescriptor> actions, IReadOnlyList<string> recall) => new()
    {
        Tools = _services.Tools?.AllDescriptors() ?? ToolBroker.Descriptors,
        Actions = actions,
        DelegateProfiles = _services.External?.ProfileNames ?? [],
        SearchProfiles = _services.External?.SearchProfileNames ?? [],
        CanBuild = _services.Tools?.CanBuild == true,
        Projects = _services.Registry.Active.Select(p => $"{p.Name} (id {p.Id}, slug {p.Slug})").ToList(),
        ResponseStyle = Preferences.PromptFragment,
        MaxAnswerChars = Preferences.MaxAnswerChars,
        PromptFragment = SelfChange?.PromptFragment(MindPrompt.PromptName),
        Constitution = AtomicFile.ReadAllTextIfExists(Path.Combine(_root.PromptsDirectory, MindPrompt.PromptName + ".md")),
        Recall = recall,
    };

    private void BeginMindLoop(TaskState task)
    {
        var mind = _services.Mind!;
        var sink = new TaskSink(this, task);
        var tools = new ToolBroker(ToolSources, sink, int.MaxValue); // the loop's own budget governs; the broker just serves
        var context = MindContextOf(task.Origin == TaskOrigin.Direct ? ActionCatalog.ForDirect : ActionCatalog.ForObserved, RecallFor(task));
        var decider = new Decider(_services.Decisions, d => RecordDecision(task, d));
        var host = new MindHost(this, task, sink, tools);
        var loop = new TaskLoop(task.TaskId, LoopOrigin(task), mind, host, context, decider,
            new LoopBudget(_settings.Orchestrator.MaxSteps, _settings.Orchestrator.MaxToolCalls), _clock);
        loop.Observe(new InputObserved(task.StartedAt, LoopOrigin(task), task.Instruction, task.ExcerptId));
        task.Loop = loop;
        task.Host = host;
        task.Plan = new TurnPlan(true, "Thinking…", [], null, [], [], mind.Name);
        Append(EventTypes.TurnStarted, new { taskId = task.TaskId, mode = OrchestratorSettings.Mind, mind = mind.Name, recall = context.Recall.Count, profiles = context.DelegateProfiles.Count, tools = context.Tools.Count, canBuild = context.CanBuild });
        task.LoopBusy = true;
        ArmMindTimeout(task);
        Drive(task, () => loop.RunAsync(task.Cts.Token));
    }

    /// <summary>Related notes and excerpts for the mind's first look ("this connects to…"); ids it may then read with a tool.</summary>
    private IReadOnlyList<string> RecallFor(TaskState task)
    {
        try
        {
            return _services.Index.Search(task.Instruction, null, 3, excludeId: task.ExcerptId)
                .Where(h => h.Id != task.ExcerptId)
                .Select(h => Truncate($"{h.Kind} {h.Id}" + (h.ProjectSlug is null ? "" : $" ({h.ProjectSlug}/{h.Type})") + ": " + h.Excerpt, 200))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return []; }
    }

    private void Drive(TaskState task, Func<Task<LoopResult?>> run)
    {
        Task<LoopResult?> running;
        try { running = run(); }
        catch (Exception ex) { running = Task.FromException<LoopResult?>(ex); }
        running.ContinueWith(t => _scheduler.Post(() => OnLoopReturned(task, t)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The stall guard: a step that takes longer than the planning timeout ends the task. Disarmed while the loop waits for the user or another process.</summary>
    private void ArmMindTimeout(TaskState task)
    {
        task.Timeout?.Dispose();
        task.Timeout = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Orchestrator.PlanningTimeoutMs), () =>
        {
            if (!_tasks.Contains(task) || !task.IsLive || task.Loop is null || !task.LoopBusy) return;
            FailTask(task, "mind_timeout", $"The mind did not answer within {_settings.Orchestrator.PlanningTimeoutMs / 1000}s.", null);
            task.Cts.Cancel();
            Notify();
        });
    }

    private void OnLoopReturned(TaskState task, Task<LoopResult?> returned)
    {
        task.LoopBusy = false;
        if (!_tasks.Contains(task) || !task.IsLive || task.Loop is null) return;
        if (returned.IsFaulted)
        {
            var ex = returned.Exception?.GetBaseException() ?? new InvalidOperationException("The loop failed.");
            FailTask(task, "mind_failed", ex.Message, ex.ToString());
            Notify();
            return;
        }
        if (returned.IsCanceled) return;
        var result = returned.Result;
        if (result is null)
        {
            task.Timeout?.Dispose();
            task.Timeout = null;
            if (task.PendingOutcomes.Count > 0)
            {
                // Everything that happened while the mind was mid-step, in one resume: delivered one per step, the mind would
                // reason over a stale stage ("wait for the test") when the build had already ended (the live run of docs/09, slice 6).
                var observations = new List<Observation>();
                string? waitFor = null;
                while (task.PendingOutcomes.Count > 0)
                {
                    var queued = task.PendingOutcomes.Dequeue();
                    observations.AddRange(queued.Observations);
                    waitFor = queued.WaitFor;
                }
                ResumeMind(task, new MoveOutcome(observations, waitFor));
            }
            else if (task.Host?.DeferredPartial is { } partial && task.Loop.WaitingFor == Waits.Delegate && task.PendingOperation is not null)
            {
                // A partial that streamed in while the mind was mid-step: shown now, so slow steps do not blind the mind to the stream.
                task.Host.DeferredPartial = null;
                ResumeMind(task, MoveOutcome.Wait(Waits.Delegate, partial));
            }
            Notify();
            return;
        }
        FinishMindTask(task, result);
        Notify();
    }

    /// <summary>Hands the loop what happened while it waited, or queues it if the loop is mid-step.</summary>
    private void ResumeMind(TaskState task, MoveOutcome outcome)
    {
        if (task.Loop is null || !task.IsLive) return;
        if (task.LoopBusy) { task.PendingOutcomes.Enqueue(outcome); return; }
        task.LoopBusy = true;
        Append(EventTypes.LoopResumed, new { taskId = task.TaskId, observations = outcome.Observations.Select(o => o.Kind), waitFor = outcome.WaitFor });
        ArmMindTimeout(task);
        Drive(task, () => task.Loop.ResumeAsync(outcome, task.Cts.Token));
    }

    private void FinishMindTask(TaskState task, LoopResult result)
    {
        task.Timeout?.Dispose();
        task.Timeout = null;
        var loop = task.Loop!;
        var read = loop.LastRead;
        var answer = result.Answer;
        var steps = result.Feed.ToList();
        if (answer is not null && answer.Length > Preferences.MaxAnswerChars)
        {
            answer = answer[..Preferences.MaxAnswerChars].TrimEnd() + "…";
            steps.Add($"Answer cut to the preferred length ({Preferences.MaxAnswerChars} chars)");
        }
        var knowledge = read is null ? null : new KnowledgeState([], [], read.Has(MindRead.NeedNewTool), read.Intent);
        task.Plan = new TurnPlan(result.Status != LoopStatus.Failed, steps.Count > 0 ? steps[^1] : result.Summary, steps, answer,
            task.Read.ToList(), [], loop.MindName, null, knowledge, loop.Consistent);

        Append(EventTypes.LoopEnded, new
        {
            taskId = task.TaskId, status = result.Status.ToString(), outcome = result.Outcome, error = result.Error,
            steps = result.Steps, toolCalls = result.ToolCalls, proposals = loop.Proposals, route = loop.Route?.Outcome,
            complexity = read?.Complexity, needs = read?.Needs, significance = read?.Significance, sensitivity = read?.Sensitivity,
            promptTokens = loop.PromptTokens, completionTokens = loop.CompletionTokens, answerChars = answer?.Length ?? 0, feed = Guarded(task, result.Feed),
        });
        RecordUsage(task, result);
        if (result.Status == LoopStatus.Failed && result.Outcome != "cancelled")
        {
            var summary = result.Outcome switch
            {
                "step_budget" => $"Relay stopped after {result.Steps} steps without finishing." + (steps.Count > 0 ? $" Last: {steps[^1]}" : ""),
                _ => "Relay's mind could not finish: " + (result.Error ?? result.Summary),
            };
            FailTask(task, result.Outcome, summary, result.Error);
            return;
        }
        FinishTask(task);
    }

    private void RecordUsage(TaskState task, LoopResult result)
    {
        if (_services.Usage is null) return;
        var loop = task.Loop!;
        var read = loop.LastRead;
        var decisions = loop.Decisions.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.Last().Outcome, StringComparer.Ordinal);
        var line = new UsageLine(_clock.UtcNow, task.TaskId, loop.Origin, loop.MindName, loop.Route?.Outcome, read?.Complexity, read?.Needs ?? [], read?.Significance,
            result.Steps, result.ToolCalls, loop.Proposals, loop.PromptTokens, loop.CompletionTokens, (long)(_clock.UtcNow - task.StartedAt).TotalMilliseconds,
            result.Status == LoopStatus.Failed ? result.Outcome : task.Proposals.Any(p => p.Status == "executed") ? "executed" : result.Outcome, decisions, task.UserResponse, null);
        var path = _services.Usage.Record(line);
        if (path is not null) Append(EventTypes.UsageRecorded, new { taskId = task.TaskId, file = Path.GetFileName(path) });
    }

    private void RecordDecision(TaskState task, DecisionRecord decision)
    {
        if (decision.Name == Decider.Narrate && decision.Outcome == Decider.Hold) return; // one line per surfaced partial, not per held one
        Append(EventTypes.DecisionMade, new { taskId = task.TaskId, decision = decision.Name, outcome = decision.Outcome, score = decision.Score, features = decision.Features, weights = decision.Weights, rationale = decision.Rationale });
    }

    // ----------------------------------------------------------------------------------------
    // Consequences, on the coordinator thread
    // ----------------------------------------------------------------------------------------

    private void OnMindStepped(TaskState task, TaskLoop loop, MindStep step)
    {
        if (!task.IsLive) return;
        ArmMindTimeout(task);
        var now = _clock.UtcNow;
        task.ModelCalls.Add(new ModelCallRecord("mind", loop.MindName, step.PromptChars, step.PromptTokens, step.CompletionTokens, step.ElapsedMs, step.Ok, step.Error, now));
        Append(EventTypes.ModelRequested, new { taskId = task.TaskId, host = "mind", model = loop.MindName, promptChars = step.PromptChars, sources = loop.Transcript.Count });
        Append(EventTypes.ModelResponded, new { taskId = task.TaskId, ok = step.Ok, chars = step.Raw?.Length ?? 0, elapsedMs = step.ElapsedMs, error = step.Error, promptTokens = step.PromptTokens, completionTokens = step.CompletionTokens });
        if (!step.Ok)
        {
            Append(EventTypes.MindFailed, new { taskId = task.TaskId, step = loop.Steps, error = step.Error, raw = Guarded(task, step.Raw) });
            Notify();
            return;
        }
        var move = step.Move!;
        var read = step.Read;
        Append(EventTypes.MindStepped, new
        {
            taskId = task.TaskId, step = loop.Steps, move = move.Type, name = MoveName(move), brief = Guarded(task, move.Brief()), feed = Guarded(task, step.Feed),
            read = read is null ? null : new { intent = Guarded(task, read.Intent), complexity = read.Complexity, needs = read.Needs, significance = read.Significance, sensitivity = read.Sensitivity, risk = new { core = read.Risk.Core, security = read.Risk.Security, loop = read.Risk.Loop, destructive = read.Risk.Destructive }, consistent = read.Consistent },
            promptTokens = step.PromptTokens, completionTokens = step.CompletionTokens, elapsedMs = step.ElapsedMs,
        });
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = Guarded(task, step.Feed) });
        // The plan is the live feed: what the mind has told the user so far, and what it has read, updated every step.
        task.Plan = new TurnPlan(true, step.Feed, loop.Feed.ToList(), task.Plan?.Answer, task.Read.ToList(), [], loop.MindName, null, task.Plan?.Knowledge, loop.Consistent);
        Notify();
    }

    private static string? MoveName(Move move) => move switch
    {
        UseToolMove t => t.Tool,
        ProposeMove p => p.Action,
        DelegateMove d => d.Profile,
        BuildMove b => b.Name,
        _ => null,
    };

    private void OnMindSaid(TaskState task, SayMove say)
    {
        if (!task.IsLive || task.Plan is null) return;
        // Narration shows as the interim answer; the final say replaces it when the loop ends.
        task.Plan = task.Plan with { Answer = say.Text };
        Notify();
    }

    private void OnMindWaiting(TaskState task, string waitingFor)
    {
        if (!task.IsLive) return;
        Append(EventTypes.LoopWaiting, new { taskId = task.TaskId, waitingFor });
        Notify();
    }

    private MoveOutcome MindUseTool(TaskState task, ToolBroker tools, UseToolMove move)
    {
        var result = tools.Call(move.Tool, move.Args);
        string? data = null;
        if (result.Ok && result.Data is not null)
        {
            try { data = JsonSerializer.Serialize(result.Data, RelayJson.Compact); }
            catch (NotSupportedException) { data = null; }
        }
        var ids = result.Hits?.Select(h => h.Id).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (result.Ok && ReadTools.Contains(move.Tool, StringComparer.Ordinal))
            foreach (var hit in result.Hits ?? [])
                if (task.Read.All(c => c.Id != hit.Id))
                    task.Read.Add(new Citation(hit.Kind, hit.Id, hit.ProjectId, hit.ProjectSlug, hit.Text.Length > 0 ? hit.Text : hit.Excerpt, hit.Span));
        return MoveOutcome.Of(new ToolObserved(_clock.UtcNow, move.Tool, move.Args, result.Ok, result.Ok ? result.Summary : result.Error ?? "failed", data, ids));
    }

    /// <summary>The tools that fetch one named thing rather than searching for candidates; what they return is what the answer stands on.</summary>
    private static readonly string[] ReadTools = ["read_note", "read_excerpt", "read_artifact"];

    /// <summary>
    /// A proposal from the mind: decided by policy exactly like any other producer's, with the filing and fundamental-operation decisions layered
    /// on top. <paramref name="coveredBy"/> names an earlier approval that covers this proposal (a follow-up turn of an approved delegate
    /// conversation): policy still validates it, and a "needs approval" verdict is then satisfied by that approval instead of a new card.
    /// </summary>
    private MoveOutcome MindPropose(TaskState task, TaskLoop loop, ProposeMove move, DecisionRecord? fof, string? coveredBy = null)
    {
        var now = _clock.UtcNow;
        var tier = PolicyEngine.TierOf(move.Action);
        var risk = tier switch { Tier.Prohibited => Risks.Prohibited, Tier.Automatic => Risks.StagingWrite, _ => move.Action == Actions.ModelRequest ? Risks.External : Risks.ControlledWrite };
        var proposal = new Proposal(Ulid.NewUlid(now), move.Action, Truncate(move.Reason, 400), move.Target, [task.SourceEventId], [], risk, tier != Tier.Automatic, loop.MindName);
        ReceiveProposal(task, proposal);
        var ps = task.Proposals.Last(p => p.Proposal.ProposalId == proposal.ProposalId);

        if (ps.Status == "pending" && coveredBy is not null && move.Action == Actions.ModelRequest)
        {
            ps.Status = "allowed";
            ps.Decision = ps.Decision with { Outcome = DecisionOutcome.Allow, Reasons = [.. ps.Decision.Reasons, "Covered by an earlier approval: " + coveredBy + "."] };
            Append(EventTypes.ProposalDecided, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = ps.Decision.Outcome.ToString(), tier = ps.Decision.Tier.ToString(), reasons = ps.Decision.Reasons, coveredBy });
        }

        // Filing: the mind names type and project with a confidence; the decision whether to ask is the decider's.
        if (ps.Status == "allowed" && move.Action == Actions.RouteNote)
        {
            var confidence = double.TryParse(move.Target.GetValueOrDefault("confidence"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, 0, 1) : 0.5;
            var filing = new Decider(_services.Decisions, d => RecordDecision(task, d)).FilingFor(confidence);
            if (filing.Outcome == Decider.Ask) { ps.Status = "pending"; ps.Decision = ps.Decision with { Outcome = DecisionOutcome.NeedsApproval, Reasons = [.. ps.Decision.Reasons, "Filing: " + filing.Rationale + " — your approval is asked."] }; }
            else if (filing.Outcome == Decider.Inbox) { ps.Status = "denied"; ps.Decision = ps.Decision with { Outcome = DecisionOutcome.Deny, Reasons = [.. ps.Decision.Reasons, "Filing: " + filing.Rationale + " — the note stays in the inbox."] }; }
            if (filing.Outcome != Decider.Auto) Append(EventTypes.ProposalDecided, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = ps.Decision.Outcome.ToString(), tier = ps.Decision.Tier.ToString(), reasons = ps.Decision.Reasons, filing = filing.Outcome });
        }
        // The fundamental-operation flag can only raise the bar: an allowed self-change the mind itself rated risky is put to the user.
        if (ps.Status == "allowed" && fof?.Outcome == Decider.Approval)
        {
            ps.Status = "pending";
            ps.Decision = ps.Decision with { Outcome = DecisionOutcome.NeedsApproval, Reasons = [.. ps.Decision.Reasons, "Fundamental-operation flag: " + fof.Rationale] };
            Append(EventTypes.ProposalDecided, new { taskId = task.TaskId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = ps.Decision.Outcome.ToString(), tier = ps.Decision.Tier.ToString(), reasons = ps.Decision.Reasons, fof = fof.Outcome });
        }

        switch (ps.Status)
        {
            case "denied":
                return MoveOutcome.Of(new PolicyObserved(now, proposal.ProposalId, proposal.Action, PolicyObserved.Denied, ps.Decision.Reasons));
            case "pending":
                task.Status = TaskStatus.AwaitingApproval;
                PersistTask(task);
                // From PLANNING on the first move, from EXECUTING when the mind read an operation's result and needs the user for the next one.
                if (task.Foreground && _state is RelayState.Planning or RelayState.Executing) Apply(Trigger.ApprovalRequired);
                if (!task.Foreground) PresentTask(task, interim: true);
                return MoveOutcome.Wait(Waits.Approval, new PolicyObserved(now, proposal.ProposalId, proposal.Action, PolicyObserved.NeedsApproval, ps.Decision.Reasons));
            default:
            {
                var reasons = ps.GrantedBy is null ? ps.Decision.Reasons : [$"Covered by standing grant {ps.GrantedBy}."];
                task.ObservedApprovals.Add(proposal.ProposalId);
                var policy = new PolicyObserved(now, proposal.ProposalId, proposal.Action, PolicyObserved.Allowed, reasons);
                var (observations, waitFor) = ExecuteMindProposal(task, ps);
                return new MoveOutcome([policy, .. observations], waitFor);
            }
        }
    }

    /// <summary>
    /// Delegation is a <c>model.request</c> proposal whose objective is the prompt the mind wrote; it needs the user's approval like any
    /// package that leaves the machine. A move with <c>reply_to</c> is the next turn of a conversation that already returned: it runs under
    /// the first request's approval when the runtime's bounds allow (turns left, no new local sources, same task), and otherwise the mind
    /// is told which bound it met and that a fresh request needs the user.
    /// </summary>
    private MoveOutcome MindDelegate(TaskState task, TaskLoop loop, DelegateMove move)
    {
        var now = _clock.UtcNow;
        var target = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = move.Profile,
            ["objective"] = move.Prompt,
            ["refs"] = string.Join(",", move.Refs),
            ["budgetTokens"] = move.BudgetTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["allowSearch"] = move.AllowSearch ? "true" : "false",
        };
        var firstLine = move.Prompt.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "delegated work";
        // One delegate request at a time per task: the reply of the one in flight is the next thing to read. Seen live: the step after
        // "started" produced a second request naming the first as a ref, and the user was asked to approve it while the reply was arriving.
        // The loop still waiting on a delegate covers both: the request in flight, and one whose reply has arrived but is queued behind this step.
        if (loop.WaitingFor == Waits.Delegate)
        {
            var inFlight = task.PendingOperation?.Proposal.ProposalId ?? task.Proposals.LastOrDefault(p => p.Proposal.Action == Actions.ModelRequest && p.Status is "executing" or "executed")?.Proposal.ProposalId ?? "in flight";
            return MoveOutcome.Of(new SystemObserved(now, $"Request {inFlight} is still answering; no second request is sent while it is. Wait for its reply (move: wait) or stop it (move: stop), then decide."));
        }
        if (move.ReplyTo is null)
        {
            // Two corrections the deterministic code makes before the card, each told to the mind: they are facts of configuration and of what
            // Relay holds, and a card the user approves only to see fail at the runtime, or a denial the mind must reason about, costs more.
            var corrections = new List<Observation>();
            if (move.AllowSearch && _services.External is { } external && !external.SearchProfileNames.Contains(move.Profile, StringComparer.Ordinal))
            {
                target["allowSearch"] = "false";
                corrections.Add(new SystemObserved(now, $"Profile '{move.Profile}' cannot search online; the request goes without search, so the package must carry every fact the delegate needs." +
                    (external.SearchProfileNames.Count > 0 ? $" Profiles that can search: {string.Join(", ", external.SearchProfileNames)}." : " No configured profile can.")));
            }
            var unknown = move.Refs.Where(r => !ReferenceExists(r)).ToList();
            if (unknown.Count > 0)
            {
                target["refs"] = string.Join(",", move.Refs.Except(unknown, StringComparer.Ordinal));
                corrections.Add(new SystemObserved(now, $"Left out of the package: {string.Join(", ", unknown)} — not ids Relay holds. Refs are the ids tools returned in this task (notes, excerpts, artifacts); " +
                    (target["refs"].Length == 0 ? "the package carries your prompt alone." : $"it carries {target["refs"]}.")));
            }
            var outcome = MindPropose(task, loop, new ProposeMove(Actions.ModelRequest, target, "Delegated by the mind: " + Truncate(firstLine, 200)), null);
            return corrections.Count == 0 ? outcome : new MoveOutcome([.. corrections, .. outcome.Observations], outcome.WaitFor);
        }

        var earlier = task.Proposals.FirstOrDefault(p => p.Proposal.ProposalId == move.ReplyTo && p.Proposal.Action == Actions.ModelRequest);
        var conversationId = earlier?.Proposal.Target.GetValueOrDefault("conversation") ?? earlier?.Proposal.ProposalId;
        var check = earlier is null || conversationId is null
            ? (Ok: false, Reason: $"Request {move.ReplyTo} is not a delegate request of this task.", Turn: 0, TurnsLeft: 0)
            : _services.External?.CanContinue(conversationId, task.TaskId, move.Refs) ?? (false, "No external runtime is configured.", 0, 0);
        if (!check.Ok)
        {
            Append(EventTypes.DelegateTurnRefused, new { taskId = task.TaskId, replyTo = move.ReplyTo, conversationId, reason = check.Reason });
            return MoveOutcome.Of(new SystemObserved(now, check.Reason + " To delegate afresh, use delegate without reply_to; the user will be asked."));
        }
        // The conversation's profile and search permission stand, whatever the mind wrote this time; only the words are new.
        target["profile"] = earlier!.Proposal.Target.GetValueOrDefault("profile") ?? move.Profile;
        target["allowSearch"] = earlier.Proposal.Target.GetValueOrDefault("allowSearch") ?? "false";
        target["budgetTokens"] = earlier.Proposal.Target.GetValueOrDefault("budgetTokens") ?? target["budgetTokens"];
        target["conversation"] = conversationId!;
        target["turn"] = check.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return MindPropose(task, loop, new ProposeMove(Actions.ModelRequest, target, $"Turn {check.Turn} in the conversation the user approved: " + Truncate(firstLine, 200)), null,
            coveredBy: $"request {conversationId} ({check.Reason})");
    }

    private MoveOutcome MindAskUser(TaskState task, AskUserMove move)
    {
        // Slice 1: the question is the interim answer and the task waits; the reply arrives through AnswerMind. The feed UI (slice 4) makes this an inline row.
        task.Plan = (task.Plan ?? new TurnPlan(true, "Question", [], null, [], [], task.Loop!.MindName)) with { Answer = move.Question + (move.Options.Count == 0 ? "" : " (" + string.Join(" / ", move.Options) + ")") };
        task.Status = TaskStatus.AwaitingApproval;
        PersistTask(task);
        if (task.Foreground && _state is RelayState.Planning or RelayState.Executing) Apply(Trigger.ApprovalRequired);
        if (!task.Foreground) PresentTask(task, interim: true);
        Append(EventTypes.TaskUserResponse, new { taskId = task.TaskId, response = "asked", question = Guarded(task, move.Question), options = move.Options.Count });
        return MoveOutcome.Wait(Waits.User);
    }

    /// <summary>The user answers a question the mind asked (or adds words to a waiting task).</summary>
    public bool AnswerMind(string taskId, string reply)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId && t.Loop is not null && t.IsLive);
        if (task is null || task.Loop!.WaitingFor != Waits.User) { _notice = "That task is not waiting for an answer."; Notify(); return false; }
        reply = (reply ?? "").Trim();
        if (reply.Length == 0) return false;
        Append(EventTypes.TaskUserResponse, new { taskId, response = "replied", chars = reply.Length });
        task.Status = TaskStatus.Planning;
        ResumeMind(task, MoveOutcome.Of(new UserObserved(_clock.UtcNow, UserObserved.Reply, reply)));
        Notify();
        return true;
    }

    private MoveOutcome MindStop(TaskState task, StopMove move, string waitingFor)
    {
        var now = _clock.UtcNow;
        if (waitingFor == Waits.Build && task.Build is { Finished: false } build) return StopBuild(task, build, move.Reason);
        var op = task.PendingOperation;
        if (op is null) return MoveOutcome.Of(new SystemObserved(now, "Nothing was running."));
        task.StopRequested = true;
        Append(EventTypes.ExecutionStopRequested, new { taskId = task.TaskId, pending = op.Proposal.ProposalId, by = "mind", reason = Guarded(task, move.Reason) });
        bool stopped;
        if (op.Proposal.Action == Actions.ModelRequest) stopped = _services.External?.Stop(op.Proposal.ProposalId, "stopped by the mind: " + move.Reason) == true;
        else { RequestWorkerStop?.Invoke(op.Proposal.ProposalId); stopped = RequestWorkerStop is not null; }
        // The completion (as stopped) arrives through CompletePendingOperation and is observed then; the loop is told the stop was requested now.
        return MoveOutcome.Wait(waitingFor, new SystemObserved(now, stopped ? $"Stop requested for {op.Proposal.Action}; its completion will follow." : "The operation could not be stopped; it will complete on its own."));
    }

    /// <summary>Runs an approved or allowed proposal now and reports it as observations; a pending operation makes the loop wait.</summary>
    private (IReadOnlyList<Observation> Observations, string? WaitFor) ExecuteMindProposal(TaskState task, ProposalState ps)
    {
        var now = _clock.UtcNow;
        ps.Status = "executing";
        ps.Capability ??= _capabilities.Issue(ps.Proposal, _clock.UtcNow);
        task.Status = TaskStatus.Executing;
        PersistTask(task);
        if (task.Foreground && _state is RelayState.Planning or RelayState.AwaitingApproval) Apply(Trigger.BeginExecution);
        var result = _executor.Execute(ps.Proposal, ps.Capability, WorldFor(task, forExecution: true), task.TaskId, this, overheard: task.Overheard);
        ps.Result = result;
        var id = ps.Proposal.ProposalId;
        var action = ps.Proposal.Action;
        if (result.Status == ExecutionStatus.Pending)
        {
            task.PendingOperation = ps;
            PersistTask(task);
            if (action == Actions.ModelRequest)
            {
                task.Host!.DelegateStarted(now);
                var turn = int.TryParse(result.Outputs.GetValueOrDefault("turn"), out var t) ? t : 1;
                return ([new DelegateObserved(now, id, result.Outputs.GetValueOrDefault("profile") ?? ps.Proposal.Target.GetValueOrDefault("profile") ?? "", DelegateObserved.Started, 0, result.Summary, null, turn)], Waits.Delegate);
            }
            return ([new ExecutionObserved(now, id, action, true, "started: " + result.Summary, result.Outputs)], Waits.Execution);
        }
        ps.Status = result.Status == ExecutionStatus.Completed ? "executed" : "failed";
        RefreshIndexAfter(ps);
        task.Status = TaskStatus.Planning;
        var executed = new ExecutionObserved(now, id, action, ps.Status == "executed", ps.Status == "executed" ? result.Summary : result.Error ?? "failed", result.Outputs);
        if (action == Actions.AddTool && ps.Status == "executed") return ([executed, ToolPromoted(task, result.Outputs.GetValueOrDefault("tool") ?? ps.Proposal.Target.GetValueOrDefault("name") ?? "")], null);
        return ([executed], null);
    }

    /// <summary>After Approve / Reject / Edit / ApproveAll: run what was approved, and tell the loop what the user decided.</summary>
    private void AdvanceMindTask(TaskState task)
    {
        var observations = new List<Observation>();
        var now = _clock.UtcNow;
        foreach (var p in task.Proposals.Where(p => p.Status is "rejected" or "edited" && !task.ObservedApprovals.Contains(p.Proposal.ProposalId)))
        {
            task.ObservedApprovals.Add(p.Proposal.ProposalId);
            // The user's words on a rejection reach the mind: "retry locally", "not that model", "later" each ask for a different next move.
            var reason = p.Status == "edited" ? "edited and re-proposed" : string.IsNullOrWhiteSpace(p.Note) || p.Note is "rejected by user" or "rejected" or "rejected from the card" ? null : p.Note;
            observations.Add(new ApprovalObserved(now, p.Proposal.ProposalId, p.Proposal.Action, false, reason));
        }
        while (true)
        {
            if (task.StopRequested) break;
            var next = task.Proposals.FirstOrDefault(p => p.Status is "approved" or "allowed" && DependenciesMet(task, p));
            if (next is null) break;
            if (task.ObservedApprovals.Add(next.Proposal.ProposalId))
                observations.Add(new ApprovalObserved(now, next.Proposal.ProposalId, next.Proposal.Action, true));
            var (executed, waitFor) = ExecuteMindProposal(task, next);
            observations.AddRange(executed);
            if (waitFor is not null) { ResumeMind(task, new MoveOutcome(observations, waitFor)); return; }
        }
        foreach (var p in task.Proposals.Where(p => p.Status is "approved" or "allowed" && DependenciesDead(task, p)))
        {
            p.Status = "skipped";
            p.Result = ExecutionResult.Fail("A prerequisite operation did not execute.");
            Append(EventTypes.ExecutionFailed, new { taskId = task.TaskId, proposalId = p.Proposal.ProposalId, action = p.Proposal.Action, error = p.Result.Error, stage = "dependency" });
            observations.Add(new ExecutionObserved(now, p.Proposal.ProposalId, p.Proposal.Action, false, p.Result.Error!, new Dictionary<string, string>()));
        }
        if (task.Proposals.Any(p => p.Status == "pending"))
        {
            // An edited proposal was re-proposed and waits again; the loop keeps waiting and hears everything at once when the user is done.
            task.Status = TaskStatus.AwaitingApproval;
            PersistTask(task);
            foreach (var o in observations) task.PendingOutcomes.Enqueue(MoveOutcome.Wait(Waits.Approval, o));
            return;
        }
        if (observations.Count == 0 && task.PendingOutcomes.Count == 0) return;
        task.Status = TaskStatus.Planning;
        PersistTask(task);
        var queued = new List<Observation>();
        while (task.PendingOutcomes.Count > 0) queued.AddRange(task.PendingOutcomes.Dequeue().Observations);
        ResumeMind(task, new MoveOutcome([.. queued, .. observations]));
    }

    /// <summary>A pending operation (worker run, external request) finished: the loop hears the result and continues.</summary>
    private void OnMindOperationCompleted(TaskState task, ProposalState op, ExecutionResult result)
    {
        var now = _clock.UtcNow;
        var id = op.Proposal.ProposalId;
        Observation observation;
        if (op.Proposal.Action == Actions.ModelRequest)
        {
            var profile = result.Outputs.GetValueOrDefault("profile") ?? op.Proposal.Target.GetValueOrDefault("profile") ?? "";
            var turn = int.TryParse(result.Outputs.GetValueOrDefault("turn"), out var t) ? t : 1;
            var turnsLeft = int.TryParse(result.Outputs.GetValueOrDefault("turnsLeft"), out var l) ? l : 0;
            if (op.Status == "executed" && result.Outputs.TryGetValue("artifactId", out var artifactId))
            {
                var artifact = _services.External?.ReadArtifactRecord(artifactId);
                var text = artifact?.Text ?? "";
                var digest = artifact?.Digest ?? Digest.Plain(text);
                if (digest.Count > 0)
                {
                    // The digest is what the feed shows of the reply: ≤3 lines under the mind's own feed sentence, the artifact behind them.
                    task.Loop!.Annotate(digest);
                    if (task.Plan is not null) task.Plan = task.Plan with { Steps = task.Loop.Feed.ToList() };
                    var digestError = result.Outputs.GetValueOrDefault("digestError");
                    Append(EventTypes.DelegateDigested, new { taskId = task.TaskId, proposalId = id, artifactId, profile, turn, lines = digest.Select(line => Guarded(task, line)).ToList(), digestBy = artifact?.DigestBy, digestTokens = result.Outputs.GetValueOrDefault("digestTokens"), digestError = string.IsNullOrEmpty(digestError) ? null : digestError });
                }
                observation = new DelegateObserved(now, id, profile, DelegateObserved.Returned, text.Length, text.Length > 2_000 ? text[..2_000] + "…(read the rest with read_artifact)" : text, artifactId, turn, turnsLeft, digest);
            }
            else observation = new DelegateObserved(now, id, profile, op.Status == "stopped" ? DelegateObserved.Stopped : DelegateObserved.Failed, 0,
                (result.Error ?? result.Summary) + (op.Status == "stopped" ? "" : " You may delegate again (the user will be asked) or continue without it: answer now with say and done=true, naming what could not be had."), null, turn);
        }
        else observation = new ExecutionObserved(now, id, op.Proposal.Action, op.Status == "executed", op.Status == "executed" ? result.Summary : result.Error ?? op.Status, result.Outputs);
        if (task.Host is not null) task.Host.DeferredPartial = null; // the reply itself supersedes any partial of it
        task.StopRequested = false;
        task.Status = TaskStatus.Planning;
        PersistTask(task);
        ResumeMind(task, MoveOutcome.Of(observation));
    }

    /// <summary>Called by the external runtime while a delegate's reply streams in. The narrate decision says whether this progress is worth a step of the mind.</summary>
    public void ReportDelegateProgress(string proposalId, string taskId, int chars, string tail)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId && t.Loop is not null && t.IsLive);
        if (task?.Host is null || task.PendingOperation?.Proposal.ProposalId != proposalId) return;
        var now = _clock.UtcNow;
        var decision = new Decider(_services.Decisions, d => RecordDecision(task, d)).NarrateFor(chars - task.Host.SurfacedChars, (now - task.Host.SurfacedAt).TotalSeconds);
        if (decision.Outcome != Decider.Surface) return;
        task.Host.SurfacedChars = chars;
        task.Host.SurfacedAt = now;
        var profile = task.PendingOperation.Proposal.Target.GetValueOrDefault("profile") ?? "";
        var partial = new DelegateObserved(now, proposalId, profile, DelegateObserved.Partial, chars, tail);
        // The mind is mid-step: keep the latest partial for when it returns, rather than stepping twice at once.
        if (task.LoopBusy || task.Loop!.Status != LoopStatus.Waiting) { task.Host.DeferredPartial = partial; return; }
        ResumeMind(task, MoveOutcome.Wait(Waits.Delegate, partial));
    }

    // ----------------------------------------------------------------------------------------
    // The host the loop talks to: marshals every call onto the coordinator thread
    // ----------------------------------------------------------------------------------------

    private sealed class MindHost : ILoopHost
    {
        private readonly SessionCoordinator _owner;
        private readonly TaskState _task;
        private readonly TaskSink _sink;
        private readonly ToolBroker _tools;

        public MindHost(SessionCoordinator owner, TaskState task, TaskSink sink, ToolBroker tools)
        {
            _owner = owner;
            _task = task;
            _sink = sink;
            _tools = tools;
        }

        public int SurfacedChars { get; set; }
        public DateTimeOffset SurfacedAt { get; set; }
        /// <summary>The newest partial that arrived while the loop was stepping; delivered when it returns to waiting.</summary>
        public DelegateObserved? DeferredPartial { get; set; }

        public void DelegateStarted(DateTimeOffset now) { SurfacedChars = 0; SurfacedAt = now; DeferredPartial = null; }

        private Task<MoveOutcome> OnCoordinator(Func<MoveOutcome> consequence)
        {
            var tcs = new TaskCompletionSource<MoveOutcome>();
            _owner._scheduler.Post(() =>
            {
                try
                {
                    if (!_task.IsLive) { tcs.SetCanceled(); return; }
                    tcs.SetResult(consequence());
                    _owner.Notify();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public void Stepped(TaskLoop loop, MindStep step) => _owner._scheduler.Post(() => _owner.OnMindStepped(_task, loop, step));
        public void Said(TaskLoop loop, SayMove move) => _owner._scheduler.Post(() => _owner.OnMindSaid(_task, move));

        /// <summary>Built-in tools answer on the coordinator thread; a promoted tool runs in the worker sandbox first (off the coordinator), then its result is recorded there.</summary>
        public async Task<MoveOutcome> UseToolAsync(TaskLoop loop, UseToolMove move, CancellationToken cancellationToken)
        {
            var tools = _owner._services.Tools;
            var package = tools?.Store.Promoted(move.Tool);
            if (tools is null || package is null) return await OnCoordinator(() => _owner.MindUseTool(_task, _tools, move)).ConfigureAwait(false);
            var missing = package.Arguments.Where(a => a.Required && string.IsNullOrWhiteSpace(move.Args.GetValueOrDefault(a.Name))).Select(a => a.Name).ToList();
            if (missing.Count > 0)
                return await OnCoordinator(() => _owner.MindBuiltToolReturned(_task, _sink, move, package, null, $"{move.Tool} needs argument(s) {string.Join(", ", missing)}: {string.Join("; ", package.Arguments.Select(a => $"{a.Name} — {a.Description}"))}")).ConfigureAwait(false);
            _sink.ToolCalled(move.Tool, move.Args);
            var run = await tools.Runner.RunAsync(package, move.Args, "task " + _task.TaskId, _task.TaskId, cancellationToken).ConfigureAwait(false);
            return await OnCoordinator(() => _owner.MindBuiltToolReturned(_task, _sink, move, package, run, null)).ConfigureAwait(false);
        }

        public Task<MoveOutcome> ProposeAsync(TaskLoop loop, ProposeMove move, DecisionRecord? fof, CancellationToken cancellationToken) => OnCoordinator(() => _owner.MindPropose(_task, loop, move, fof));
        public Task<MoveOutcome> DelegateAsync(TaskLoop loop, DelegateMove move, CancellationToken cancellationToken) => OnCoordinator(() => _owner.MindDelegate(_task, loop, move));
        public Task<MoveOutcome> BuildAsync(TaskLoop loop, BuildMove move, DecisionRecord fof, CancellationToken cancellationToken) => OnCoordinator(() => _owner.MindBuild(_task, loop, move, fof));
        public Task<MoveOutcome> AskUserAsync(TaskLoop loop, AskUserMove move, CancellationToken cancellationToken) => OnCoordinator(() => _owner.MindAskUser(_task, move));
        public Task<MoveOutcome> StopAsync(TaskLoop loop, StopMove move, string waitingFor, CancellationToken cancellationToken) => OnCoordinator(() => _owner.MindStop(_task, move, waitingFor));
        public void Waiting(TaskLoop loop, string waitingFor) => _owner._scheduler.Post(() => _owner.OnMindWaiting(_task, waitingFor));
        public void Ended(TaskLoop loop, LoopResult result) { /* the result returns through the loop's task; see OnLoopReturned */ }
    }
}
