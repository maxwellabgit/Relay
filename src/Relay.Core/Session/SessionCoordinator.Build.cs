using Relay.Core.Decisions;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.Tools;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Core.Session;

/// <summary>
/// Capability building (docs/09, slice 6): the mind's <c>build</c> move becomes a <c>build_tool</c> proposal the
/// user must approve before any drafting runs — the same bar as other self-change. After approval the builder
/// drafts with the fixed build prompt, runs tests in the worker sandbox and retries once; each stage comes back
/// as a <see cref="BuildObserved"/>. On success the draft is promoted under that approval (one reversible
/// change set, no second card).
/// </summary>
public sealed partial class SessionCoordinator
{
    private sealed class BuildState
    {
        public required string Name { get; init; }
        public required BuildMove Move { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        /// <summary>The approved <c>build_tool</c> proposal this run fulfills; promotion and completion attach to it.</summary>
        public required ProposalState Proposal { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public bool Finished { get; set; }
        public bool StopRequested { get; set; }
        public ToolPackage? Package { get; set; }
        /// <summary>The tested observation is delivered together with promotion, so the mind spends one step on both.</summary>
        public BuildObserved? Tested { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>
    /// The mind asked to build: put a <c>build_tool</c> card to the user. Drafting starts only after they approve.
    /// </summary>
    private MoveOutcome MindBuild(TaskState task, TaskLoop loop, BuildMove move, DecisionRecord fof)
    {
        var now = _clock.UtcNow;
        var tools = _services.Tools;
        if (tools is null || !tools.CanBuild)
            return MoveOutcome.Of(new BuildObserved(now, move.Name, BuildObserved.Unavailable, tools is null ? "No sandbox is configured, so tools cannot be built here. Tell the user which tool would be needed." : "No model is configured to draft tools. Tell the user which tool would be needed."));
        if (task.Build is { Finished: false } running)
            return MoveOutcome.Wait(Waits.Build, new BuildObserved(now, move.Name, BuildObserved.Failed, $"A build of '{running.Name}' is already running in this task; wait for it or stop it."));
        if (task.Proposals.Any(p => p.Proposal.Action == Actions.BuildTool && p.Status == "pending"
            && string.Equals(p.Proposal.Target.GetValueOrDefault("name"), move.Name, StringComparison.OrdinalIgnoreCase)))
            return MoveOutcome.Wait(Waits.Approval, new SystemObserved(now, $"A build of '{move.Name}' is already awaiting your approval. Wait for that decision."));
        if (tools.Store.IsPromoted(move.Name) || Orchestration.ToolBroker.Descriptors.Any(d => d.Name == move.Name))
            return MoveOutcome.Of(new BuildObserved(now, move.Name, BuildObserved.Failed, $"A tool named '{move.Name}' already exists; call it with use_tool instead of building it."));
        // The drafter is deterministic: the same contract fails the same way. A second build in the same task must change the contract
        // (name, inputs or outputs); otherwise the mind is told to answer what it can or ask the user (docs/09: delegation of a failed build is slice 5).
        if (task.Build is { Finished: true, Package: null or { Tested: false } } failed && SameContract(failed.Move, move))
            return MoveOutcome.Of(new BuildObserved(now, move.Name, BuildObserved.Failed,
                $"A build of '{failed.Name}' with this same contract already failed in this task ({failed.Attempts} attempt(s)); the drafter would produce the same result. " +
                "Do not build it again: answer what you can, say which tool would be needed, or ask the user. A different contract (clear inputs and outputs, a general tool with the varying part as an argument) may be built."));

        var target = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = move.Name,
            ["inputs"] = move.Inputs,
            ["outputs"] = move.Outputs,
            ["benefit"] = Truncate(move.Justification, 400),
            ["permissions"] = "Drafts a tool with the local model, runs its tests in the worker sandbox (no network, files or processes beyond declared host functions), and promotes it into tools/ on success as a reversible change set.",
            ["scope"] = Truncate($"One new read-only tool: {move.Name} — inputs: {move.Inputs}; outputs: {move.Outputs}", 400),
            ["acceptance"] = "After approval, the tool is callable with use_tool once tests pass; on failure nothing is promoted and the draft stays in staging.",
        };
        return MindPropose(task, loop, new ProposeMove(Actions.BuildTool, target, Truncate(move.Justification.Length > 0 ? move.Justification : $"Build tool {move.Name}", 400)), fof);
    }

    private static bool SameContract(BuildMove a, BuildMove b)
        => string.Equals(a.Name, b.Name, StringComparison.Ordinal)
           && string.Equals(a.Inputs.Trim(), b.Inputs.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Outputs.Trim(), b.Outputs.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>An approved <c>build_tool</c> proposal: start drafting now and keep the loop on <see cref="Waits.Build"/>.</summary>
    private (IReadOnlyList<Observation> Observations, string? WaitFor) BeginApprovedBuild(TaskState task, ProposalState ps)
    {
        var now = _clock.UtcNow;
        var tools = _services.Tools;
        var name = ps.Proposal.Target.GetValueOrDefault("name") ?? "";
        if (tools is null || !tools.CanBuild)
        {
            ps.Status = "failed";
            ps.Result = ExecutionResult.Fail("Tool building is not configured.");
            return ([new BuildObserved(now, name, BuildObserved.Unavailable, ps.Result.Error!)], null);
        }
        var move = new BuildMove(
            name,
            ps.Proposal.Reason,
            ps.Proposal.Target.GetValueOrDefault("inputs") ?? "",
            ps.Proposal.Target.GetValueOrDefault("outputs") ?? "");
        var build = new BuildState { Name = move.Name, Move = move, StartedAt = now, Proposal = ps };
        task.Build = build;
        task.PendingOperation = ps;
        Append(EventTypes.ToolBuildStarted, new
        {
            taskId = task.TaskId, tool = move.Name, proposalId = ps.Proposal.ProposalId,
            justification = Guarded(task, move.Justification), inputs = Guarded(task, move.Inputs), outputs = Guarded(task, move.Outputs),
            drafter = tools.Builder.DrafterName, sandbox = tools.Runner.Host.Description, attempts = ToolBuilder.MaxAttempts,
        });
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Building the tool {move.Name}: drafting with {tools.Builder.DrafterName}" });
        PersistTask(task);
        var token = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, build.Cts.Token).Token;
        _ = RunBuildAsync(task, build, tools, token);
        return ([new BuildObserved(now, move.Name, BuildObserved.Started,
            $"Drafting with {tools.Builder.DrafterName}; the draft's tests then run in the sandbox. Each stage arrives as an observation: wait for them, or stop the build.")], Waits.Build);
    }

    /// <summary>Off the coordinator thread: the model drafts, the sandbox tests. Every stage is posted back; the loop hears each one.</summary>
    private async Task RunBuildAsync(TaskState task, BuildState build, ToolRuntime tools, CancellationToken cancellationToken)
    {
        BuildOutcome outcome;
        try
        {
            outcome = await tools.Builder.BuildAsync(build.Move, task.Instruction, task.TaskId, progress =>
            {
                _scheduler.Post(() => OnBuildProgress(task, build, progress));
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _scheduler.Post(() => OnBuildStopped(task, build));
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            outcome = new BuildOutcome(false, null, "The build failed: " + ex.Message, build.Attempts, 0, 0, []);
        }
        _scheduler.Post(() => OnBuildFinished(task, build, outcome));
    }

    /// <summary>Coordinator thread. A stage of the build: recorded, shown in the feed, and handed to the loop as an observation.</summary>
    private void OnBuildProgress(TaskState task, BuildState build, BuildProgress progress)
    {
        if (!_tasks.Contains(task) || !task.IsLive || build.Finished) return;
        var now = _clock.UtcNow;
        build.Attempts = progress.Attempt;
        if (build.StopRequested)
        {
            // The mind asked for the end; the stage that was in flight is recorded but the mind hears only the stop.
            Append(EventTypes.ToolBuildFailed, new { taskId = task.TaskId, tool = build.Name, attempt = progress.Attempt, stage = progress.Stage, detail = Guarded(task, progress.Detail), stopping = true });
            return;
        }
        switch (progress.Stage)
        {
            case BuildObserved.Drafted:
            {
                var p = progress.Package!;
                build.Package = p;
                Append(EventTypes.ToolBuildDrafted, new
                {
                    taskId = task.TaskId, tool = p.Name, attempt = progress.Attempt, description = Guarded(task, p.Description), arguments = p.Arguments.Select(a => a.Required ? a.Name : a.Name + "?"),
                    hostFunctions = p.HostFunctionNames, sourceChars = p.Source.Length, sourceSha256 = p.SourceSha256, tests = p.Tests.Count, builtBy = p.BuiltBy, draft = _services.Tools!.Store.DraftPath(p.Name),
                });
                Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Drafted {p.Name}; running its {p.Tests.Count} test(s) in the sandbox" });
                ResumeMind(task, MoveOutcome.Wait(Waits.Build, new BuildObserved(now, p.Name, BuildObserved.Drafted, progress.Detail + " · testing now; wait for the result or stop.")));
                break;
            }
            case BuildObserved.Tested:
            {
                var p = progress.Package!;
                build.Package = p;
                Append(EventTypes.ToolBuildTested, new { taskId = task.TaskId, tool = p.Name, attempt = progress.Attempt, passed = true, detail = progress.Detail, testedSha256 = p.TestedSha256 });
                Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"{p.Name} passed its tests; promoting under the approval you already gave" });
                build.Tested = new BuildObserved(now, p.Name, BuildObserved.Tested, progress.Detail);
                break; // delivered with promotion in OnBuildFinished
            }
            case BuildObserved.Failed:
            {
                var retrying = progress.Attempt < progress.Attempts;
                Append(EventTypes.ToolBuildFailed, new { taskId = task.TaskId, tool = build.Name, attempt = progress.Attempt, detail = Guarded(task, progress.Detail), retrying });
                if (!retrying) break; // the final failure is reported by OnBuildFinished
                Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"The first draft of {build.Name} failed its tests; drafting again with the failure" });
                ResumeMind(task, MoveOutcome.Wait(Waits.Build, new BuildObserved(now, build.Name, BuildObserved.Failed, progress.Detail + $" · retrying (attempt {progress.Attempt + 1} of {progress.Attempts}); wait or stop.")));
                break;
            }
        }
        Notify();
    }

    /// <summary>Coordinator thread. The build ended: promote under the approved build_tool, or report the failure.</summary>
    private void OnBuildFinished(TaskState task, BuildState build, BuildOutcome outcome)
    {
        if (build.Finished) return;
        build.Finished = true;
        if (!_tasks.Contains(task) || !task.IsLive || task.Loop is null) return;
        var now = _clock.UtcNow;
        var ps = build.Proposal;
        if (task.PendingOperation?.Proposal.ProposalId == ps.Proposal.ProposalId)
            task.PendingOperation = null;

        if (!outcome.Ok || outcome.Package is null)
        {
            ps.Status = "failed";
            ps.Result = ExecutionResult.Fail(outcome.Summary);
            Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Could not build {build.Name}" });
            Append(EventTypes.ExecutionFailed, new { taskId = task.TaskId, proposalId = ps.Proposal.ProposalId, action = Actions.BuildTool, error = outcome.Summary, stage = "build" });
            var offer = _services.External is { ProfileNames.Count: > 0 }
                ? " If a delegate could settle this, delegate now: the user is asked and can approve it, or reject it with words for you (such as \"retry locally\")."
                : "";
            ResumeMind(task, MoveOutcome.Of(
                new BuildObserved(now, build.Name, BuildObserved.Failed, outcome.Summary + " The draft stays in staging. Do not build the same contract again; answer what you can and say which tool would be needed." + offer)));
            Notify();
            return;
        }

        var p = outcome.Package;
        var runs = string.Join(", ", outcome.Tests.Select(t => t.RunId));
        var promoteTarget = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = p.Name,
            ["sourceSha256"] = p.SourceSha256,
            ["benefit"] = ps.Proposal.Target.GetValueOrDefault("benefit") ?? Truncate(build.Move.Justification, 400),
            ["permissions"] = ps.Proposal.Target.GetValueOrDefault("permissions") ?? Truncate($"Runs only in the worker sandbox; may ask the machine for: {(p.HostFunctionNames.Count == 0 ? "nothing" : string.Join(", ", p.HostFunctionNames))}.", 400),
            ["scope"] = ps.Proposal.Target.GetValueOrDefault("scope") ?? Truncate($"Adds one read-only tool: {p.Name}({string.Join(", ", p.Arguments.Select(a => a.Name))}) — {p.Description}", 400),
            ["acceptance"] = Truncate($"{outcome.Summary} (runs {runs}).", 400),
        };
        var promoteProposal = new Proposal(Ulid.NewUlid(now), Actions.AddTool, ps.Proposal.Reason, promoteTarget, ps.Proposal.SourceEventIds, [], Risks.ControlledWrite, true, ps.Proposal.ProposedBy);
        var decision = new Decision(DecisionOutcome.Allow, Tier.RequiresApproval, ["Covered by the approved build_tool proposal " + ps.Proposal.ProposalId + "."], promoteTarget);
        var promote = _services.Tools!.Promote(promoteProposal, decision, task.TaskId, this);
        if (promote.Status != ExecutionStatus.Completed)
        {
            ps.Status = "failed";
            ps.Result = promote;
            Append(EventTypes.ExecutionFailed, new { taskId = task.TaskId, proposalId = ps.Proposal.ProposalId, action = Actions.BuildTool, error = promote.Error, stage = "promote" });
            ResumeMind(task, MoveOutcome.Of(
                build.Tested ?? new BuildObserved(now, build.Name, BuildObserved.Tested, outcome.Summary),
                new BuildObserved(now, build.Name, BuildObserved.Failed, promote.Error ?? "Promotion failed after tests passed.")));
            Notify();
            return;
        }

        ps.Status = "executed";
        ps.Result = ExecutionResult.Ok($"Built and promoted '{p.Name}'", promote.Outputs);
        Append(EventTypes.ExecutionCompleted, new { taskId = task.TaskId, proposalId = ps.Proposal.ProposalId, action = Actions.BuildTool, summary = ps.Result.Summary, outputs = promote.Outputs });
        var observations = new List<Observation>();
        if (build.Tested is not null) observations.Add(build.Tested);
        observations.Add(new ExecutionObserved(now, ps.Proposal.ProposalId, Actions.BuildTool, true, ps.Result.Summary, promote.Outputs));
        observations.Add(ToolPromoted(task, p.Name));
        ResumeMind(task, new MoveOutcome(observations));
        Notify();
    }

    private void OnBuildStopped(TaskState task, BuildState build)
    {
        if (build.Finished) return;
        build.Finished = true;
        if (!_tasks.Contains(task) || !task.IsLive) return;
        var ps = build.Proposal;
        if (task.PendingOperation?.Proposal.ProposalId == ps.Proposal.ProposalId)
            task.PendingOperation = null;
        ps.Status = "failed";
        ps.Result = ExecutionResult.Fail(build.StopRequested ? "Stopped at your request." : "The build was cancelled.");
        Append(EventTypes.ToolBuildStopped, new { taskId = task.TaskId, tool = build.Name, attempt = build.Attempts, byMind = build.StopRequested, proposalId = ps.Proposal.ProposalId });
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Stopped building {build.Name}" });
        ResumeMind(task, MoveOutcome.Of(new BuildObserved(_clock.UtcNow, build.Name, BuildObserved.Stopped, build.StopRequested ? "Stopped at your request; the draft, if any, stays in staging." : "The build was cancelled.")));
        Notify();
    }

    private MoveOutcome StopBuild(TaskState task, BuildState build, string reason)
    {
        build.StopRequested = true;
        Append(EventTypes.ExecutionStopRequested, new { taskId = task.TaskId, build = build.Name, by = "mind", reason = Guarded(task, reason) });
        build.Cts.Cancel();
        return MoveOutcome.Wait(Waits.Build, new SystemObserved(_clock.UtcNow, $"Stop requested for the build of {build.Name}; its end will follow."));
    }

    /// <summary>After a tool is promoted: the mind's tool list gains the tool now, and the transcript shows the build's last stage.</summary>
    private BuildObserved ToolPromoted(TaskState task, string name)
    {
        var tools = _services.Tools!;
        if (task.Loop is not null) task.Loop.Context.Tools = tools.AllDescriptors();
        var package = tools.Store.Promoted(name);
        var usage = package is null ? name : $"{name}({string.Join(", ", package.Arguments.Select(a => a.Required ? a.Name : a.Name + "?"))})";
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Added the tool {name}" });
        return new BuildObserved(_clock.UtcNow, name, BuildObserved.Promoted, $"The tool is available now: use_tool {usage}. Call it to finish the task." + (package is null ? "" : $" Arguments: {string.Join("; ", package.Arguments.Select(a => $"{a.Name} — {a.Description}"))}."));
    }

    /// <summary>Coordinator thread. A promoted tool ran in the sandbox (or could not be run): recorded like any tool call and observed by the loop.</summary>
    private MoveOutcome MindBuiltToolReturned(TaskState task, TaskSink sink, UseToolMove move, ToolPackage package, ToolRunResult? run, string? error)
    {
        var now = _clock.UtcNow;
        if (run is null)
        {
            sink.ToolCalled(move.Tool, move.Args);
            sink.ToolReturned(move.Tool, false, error ?? "failed", 0);
            return MoveOutcome.Of(new ToolObserved(now, move.Tool, move.Args, false, error ?? "failed", null, []));
        }
        Append(EventTypes.ToolRan, new { taskId = task.TaskId, tool = move.Tool, runId = run.RunId, ok = run.Ok, hostCalls = run.HostCalls, denied = run.Denied, elapsedMs = run.ElapsedMs, chars = run.ResultJson?.Length ?? 0, error = run.Error, logs = run.Logs.Count, sourceSha256 = package.SourceSha256 });
        foreach (var e in run.Events.Where(e => e.Type == EventTypes.AgentRunToolDenied)) Append(e.Type, e.Data);
        sink.ToolReturned(move.Tool, run.Ok, run.Summary(200), run.Ok ? 1 : 0);
        var data = run.Ok ? run.ResultJson : null;
        return MoveOutcome.Of(new ToolObserved(now, move.Tool, move.Args, run.Ok, run.Ok ? "ok · " + run.Summary(300) : run.Error ?? "failed", data, []));
    }
}
