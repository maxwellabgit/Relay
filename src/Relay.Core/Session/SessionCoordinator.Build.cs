using Relay.Core.Decisions;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.Tools;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Core.Session;

/// <summary>
/// Capability building (docs/09, slice 6): the mind's <c>build</c> move as a pending operation the loop
/// observes stage by stage. The builder drafts a package with the fixed build prompt, runs its tests in the
/// worker sandbox and retries once; each stage comes back as a <see cref="BuildObserved"/> that buys the
/// mind one step (narrate, keep waiting, or stop). A tested draft becomes one <c>add_tool</c> proposal —
/// the single approval — and its execution is a change set the user can revert. The promoted tool joins
/// the mind's tool list at once and runs in the same sandbox when the mind calls it.
/// </summary>
public sealed partial class SessionCoordinator
{
    private sealed class BuildState
    {
        public required string Name { get; init; }
        public required BuildMove Move { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public bool Finished { get; set; }
        public bool StopRequested { get; set; }
        public ToolPackage? Package { get; set; }
        /// <summary>The tested observation is delivered together with the promotion proposal, so the mind spends one step on both.</summary>
        public BuildObserved? Tested { get; set; }
        public int Attempts { get; set; }
    }

    private MoveOutcome MindBuild(TaskState task, TaskLoop loop, BuildMove move, DecisionRecord fof)
    {
        var now = _clock.UtcNow;
        var tools = _services.Tools;
        if (tools is null || !tools.CanBuild)
            return MoveOutcome.Of(new BuildObserved(now, move.Name, BuildObserved.Unavailable, tools is null ? "No sandbox is configured, so tools cannot be built here. Tell the user which tool would be needed." : "No model is configured to draft tools. Tell the user which tool would be needed."));
        if (task.Build is { Finished: false } running)
            return MoveOutcome.Wait(Waits.Build, new BuildObserved(now, move.Name, BuildObserved.Failed, $"A build of '{running.Name}' is already running in this task; wait for it or stop it."));
        if (tools.Store.IsPromoted(move.Name) || Orchestration.ToolBroker.Descriptors.Any(d => d.Name == move.Name))
            return MoveOutcome.Of(new BuildObserved(now, move.Name, BuildObserved.Failed, $"A tool named '{move.Name}' already exists; call it with use_tool instead of building it."));

        var build = new BuildState { Name = move.Name, Move = move, StartedAt = now };
        task.Build = build;
        Append(EventTypes.ToolBuildStarted, new
        {
            taskId = task.TaskId, tool = move.Name, justification = Guarded(task, move.Justification), inputs = Guarded(task, move.Inputs), outputs = Guarded(task, move.Outputs),
            drafter = tools.Builder.DrafterName, sandbox = tools.Runner.Host.Description, fof = fof.Outcome, attempts = ToolBuilder.MaxAttempts,
        });
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Building the tool {move.Name}: drafting with {tools.Builder.DrafterName}" });
        PersistTask(task);
        var token = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, build.Cts.Token).Token;
        _ = RunBuildAsync(task, build, tools, token);
        return MoveOutcome.Wait(Waits.Build, new BuildObserved(now, move.Name, BuildObserved.Started,
            $"Drafting with {tools.Builder.DrafterName}; the draft's tests then run in the sandbox, and the user is asked once before it is promoted. Each stage arrives as an observation: wait for them, or stop the build."));
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
                Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"{p.Name} passed its tests; asking you before it is added" });
                build.Tested = new BuildObserved(now, p.Name, BuildObserved.Tested, progress.Detail);
                break; // delivered with the promotion proposal in OnBuildFinished
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

    /// <summary>Coordinator thread. The build ended: a tested draft becomes the one add_tool proposal, or the failure is what the mind hears.</summary>
    private void OnBuildFinished(TaskState task, BuildState build, BuildOutcome outcome)
    {
        if (build.Finished) return;
        build.Finished = true;
        if (!_tasks.Contains(task) || !task.IsLive || task.Loop is null) return;
        var now = _clock.UtcNow;
        if (!outcome.Ok || outcome.Package is null)
        {
            Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Could not build {build.Name}" });
            ResumeMind(task, MoveOutcome.Of(new BuildObserved(now, build.Name, BuildObserved.Failed, outcome.Summary + " The draft stays in staging. Answer what you can and say which tool would be needed.")));
            Notify();
            return;
        }
        var p = outcome.Package;
        var runs = string.Join(", ", outcome.Tests.Select(t => t.RunId));
        var target = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = p.Name,
            ["sourceSha256"] = p.SourceSha256,
            ["benefit"] = Truncate(build.Move.Justification, 400),
            ["permissions"] = Truncate($"Runs only in the worker sandbox (no files, network or processes); may ask the machine for: {(p.HostFunctionNames.Count == 0 ? "nothing" : string.Join(", ", p.HostFunctionNames))}.", 400),
            ["scope"] = Truncate($"Adds one read-only tool the mind may call: {p.Name}({string.Join(", ", p.Arguments.Select(a => a.Name))}) — {p.Description}", 400),
            ["acceptance"] = Truncate($"{outcome.Summary} (runs {runs}).", 400),
        };
        var propose = new ProposeMove(Actions.AddTool, target, Truncate($"Built for this task: {build.Move.Justification}", 400));
        var proposal = MindPropose(task, task.Loop, propose, null);
        var observations = new List<Observation>();
        if (build.Tested is not null) observations.Add(build.Tested);
        observations.AddRange(proposal.Observations);
        ResumeMind(task, new MoveOutcome(observations, proposal.WaitFor));
        Notify();
    }

    private void OnBuildStopped(TaskState task, BuildState build)
    {
        if (build.Finished) return;
        build.Finished = true;
        if (!_tasks.Contains(task) || !task.IsLive) return;
        Append(EventTypes.ToolBuildStopped, new { taskId = task.TaskId, tool = build.Name, attempt = build.Attempts, byMind = build.StopRequested });
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

    /// <summary>After add_tool executed: the mind's tool list gains the tool now, and the transcript shows the build's last stage.</summary>
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
