using System.Security.Cryptography;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Agents;

/// <summary>
/// Runs approved <c>launch_worker</c> proposals and applies their output on a later
/// <c>apply_patch</c>. The worker process is confined by the host; this class enforces the wall
/// clock, drives the broker, verifies inputs and required outputs when the run ends, and hands
/// the result back to the coordinator on its own thread. Nothing here touches the ledger from a
/// background thread: every record goes through <see cref="IScheduler.Post"/>.
/// </summary>
public sealed class WorkerRuntime : IWorkerOperations
{
    private sealed class ActiveRun
    {
        public required AgentRunSpec Spec { get; init; }
        public required WorkerBroker Broker { get; init; }
        public required IExecutionSink Sink { get; init; }
        public required string ProposalId { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public IWorkerProcess? Process { get; set; }
        public IDisposable? StartTimer { get; set; }
        public IDisposable? Deadline { get; set; }
        public IDisposable? ExitGrace { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public string? TerminatedReason { get; set; }
        public int? ExitCode { get; set; }
        public bool Finished { get; set; }
        public int Logs { get; set; }
    }

    private const int MaxLogsPerRun = 200;
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);

    private readonly DataRoot _root;
    private readonly ProjectRegistry _registry;
    private readonly IWorkerHost _host;
    private readonly IClock _clock;
    private readonly IScheduler _scheduler;
    private readonly WorkerSettings _settings;
    private readonly Dictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);

    public WorkerRuntime(DataRoot root, ProjectRegistry registry, IWorkerHost host, IClock clock, IScheduler scheduler, WorkerSettings settings)
    {
        _root = root;
        _registry = registry;
        _host = host;
        _clock = clock;
        _scheduler = scheduler;
        _settings = settings;
    }

    /// <summary>Set by the composition root: (proposalId, result) → coordinator.CompletePendingOperation. Invoked on the coordinator thread.</summary>
    public Action<string, ExecutionResult>? Completed { get; set; }

    public IWorkerHost Host => _host;
    public int ActiveRuns => _active.Count;

    // ----------------------------------------------------------------------------------------
    // launch_worker
    // ----------------------------------------------------------------------------------------

    public ExecutionResult Launch(Proposal proposal, Decision decision, string turnId, IExecutionSink sink)
    {
        var project = _registry.ById(decision.NormalizedTarget["projectId"]);
        if (project is null) return ExecutionResult.Fail("Project vanished before the worker could start.");
        if (_active.ContainsKey(proposal.ProposalId)) return ExecutionResult.Fail("This proposal already has a running worker.");

        var now = _clock.UtcNow;
        var runId = Ulid.NewUlid(now);
        AgentRunSpec spec;
        try
        {
            spec = AgentRunSpec.Prepare(_root, project, runId, proposal.ProposalId, turnId, decision.NormalizedTarget["task"], decision.NormalizedTarget["objective"], _settings, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExecutionResult.Fail("Could not stage the worker run: " + ex.Message);
        }
        new AgentRunStatus(runId, project.Id, spec.Task, "launched", null, null, [], 0, null, now, null).Save(spec.StagingPath);
        sink.Record(EventTypes.AgentRunLaunched, new
        {
            runId, proposalId = proposal.ProposalId, turnId, projectId = project.Id, projectSlug = project.Slug, task = spec.Task, objective = spec.Objective,
            inputs = spec.Inputs.Count, inputBytes = spec.Inputs.Sum(i => i.Bytes), staging = spec.StagingPath, tools = spec.ToolAllowlist, network = spec.Network,
            limits = spec.Limits, host = _host.Description,
        });

        var run = new ActiveRun { Spec = spec, Broker = new WorkerBroker(spec), Sink = sink, ProposalId = proposal.ProposalId, StartedAt = now };
        _active[proposal.ProposalId] = run;
        // Deferred one tick so the coordinator has registered the pending operation before anything can complete it.
        run.StartTimer = _scheduler.Schedule(TimeSpan.Zero, () => Start(run));
        return ExecutionResult.Pending($"Worker {runId} running: {spec.Objective}", new Dictionary<string, string> { ["runId"] = runId, ["staging"] = spec.StagingPath });
    }

    private void Start(ActiveRun run)
    {
        run.StartTimer = null;
        if (run.Finished) return;
        try
        {
            run.Process = _host.Start(run.Spec);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or FileNotFoundException)
        {
            run.TerminatedReason = "could not start the worker: " + ex.Message;
            Finish(run);
            return;
        }
        run.Deadline = _scheduler.Schedule(TimeSpan.FromSeconds(run.Spec.Limits.WallClockSeconds), () => Terminate(run, $"wall clock limit of {run.Spec.Limits.WallClockSeconds}s reached"));
        _ = PumpAsync(run);
    }

    private async Task PumpAsync(ActiveRun run)
    {
        var process = run.Process!;
        var ct = run.Cts.Token;
        try
        {
            await process.WriteLineAsync(run.Spec.ToSpecMessage(), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var line = await process.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // worker closed stdout
                if (line.Length == 0) continue;
                var turn = run.Broker.Handle(line);
                FlushEvents(run);
                if (turn.Log is not null && run.Logs < MaxLogsPerRun)
                {
                    run.Logs++;
                    var text = turn.Log;
                    _scheduler.Post(() => run.Sink.Record(EventTypes.AgentRunLog, new { runId = run.Spec.RunId, text }));
                }
                if (turn.Reply is not null) await process.WriteLineAsync(turn.Reply, ct).ConfigureAwait(false);
                if (turn.Finished) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, killed, or the pipe broke: the exit path below decides what that means.
        }

        // The worker is expected to exit on its own after done/failed; give it a grace period, then kill.
        _scheduler.Post(() =>
        {
            if (run.Finished) return;
            if (process.Exited.IsCompleted) { run.ExitCode = process.Exited.Result; Finish(run); return; }
            run.ExitGrace = _scheduler.Schedule(ExitGracePeriod, () => { if (!run.Finished) { process.Kill("did not exit after finishing"); run.ExitCode = -1; Finish(run); } });
            process.Exited.ContinueWith(t => _scheduler.Post(() => { if (!run.Finished) { run.ExitCode = t.IsCompletedSuccessfully ? t.Result : -1; Finish(run); } }), TaskScheduler.Default);
        });
    }

    private void FlushEvents(ActiveRun run)
    {
        if (run.Broker.Events.Count == 0) return;
        var events = run.Broker.Events.ToArray();
        run.Broker.Events.Clear();
        _scheduler.Post(() => { foreach (var e in events) run.Sink.Record(e.Type, e.Data); });
    }

    private void Terminate(ActiveRun run, string reason)
    {
        if (run.Finished || run.TerminatedReason is not null) return;
        run.TerminatedReason = reason;
        run.Cts.Cancel();
        if (run.Process is { } p)
        {
            try { p.WriteLineAsync(WorkerBroker.StopMessage(reason), CancellationToken.None).Wait(TimeSpan.FromMilliseconds(200)); } catch (Exception) { }
            p.Kill(reason);
            run.ExitCode ??= -1;
        }
        Finish(run);
    }

    /// <summary>Coordinator thread. Decides what the run amounts to, records it, persists the status file, and resumes the turn.</summary>
    private void Finish(ActiveRun run)
    {
        if (run.Finished) return;
        run.Finished = true;
        run.StartTimer?.Dispose();
        run.Deadline?.Dispose();
        run.ExitGrace?.Dispose();
        _active.Remove(run.ProposalId);
        FlushEvents(run);

        var spec = run.Spec;
        var broker = run.Broker;
        var now = _clock.UtcNow;
        string state;
        ExecutionResult result;
        var inputProblems = spec.VerifyInputsUnchanged();
        var missing = spec.RequiredOutputs.Where(o => !File.Exists(Path.Combine(spec.StagingPath, o.Replace('/', Path.DirectorySeparatorChar)))).ToList();

        if (run.TerminatedReason is not null)
        {
            state = "terminated";
            run.Sink.Record(EventTypes.AgentRunTerminated, new { runId = spec.RunId, reason = run.TerminatedReason, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode, partialOutputs = ListOutputs(spec) });
            result = ExecutionResult.Fail($"Worker {spec.RunId} terminated: {run.TerminatedReason}. Partial output stays in {spec.OutPath}.");
        }
        else if (inputProblems.Count > 0)
        {
            state = "terminated";
            var reason = "inputs were modified during the run: " + string.Join("; ", inputProblems);
            run.Sink.Record(EventTypes.AgentRunTerminated, new { runId = spec.RunId, reason, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode });
            result = ExecutionResult.Fail($"Worker {spec.RunId} escaped the broker: {reason}. Its output was not accepted.");
        }
        else if (broker.Failed)
        {
            state = "failed";
            run.Sink.Record(EventTypes.AgentRunCompleted, new { runId = spec.RunId, ok = false, error = broker.Error, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode });
            result = ExecutionResult.Fail($"Worker {spec.RunId} failed: {broker.Error}");
        }
        else if (!broker.Done)
        {
            state = "terminated";
            var reason = $"worker exited (code {run.ExitCode?.ToString() ?? "?"}) without reporting done";
            run.Sink.Record(EventTypes.AgentRunTerminated, new { runId = spec.RunId, reason, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode });
            result = ExecutionResult.Fail($"Worker {spec.RunId}: {reason}.");
        }
        else if (missing.Count > 0)
        {
            state = "failed";
            var error = "required output missing: " + string.Join(", ", missing);
            run.Sink.Record(EventTypes.AgentRunCompleted, new { runId = spec.RunId, ok = false, error, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode });
            result = ExecutionResult.Fail($"Worker {spec.RunId} finished but {error}.");
        }
        else
        {
            state = "completed";
            var outputs = ListOutputs(spec);
            var hashes = outputs.ToDictionary(o => o, o => Sha256File(Path.Combine(spec.StagingPath, o.Replace('/', Path.DirectorySeparatorChar))));
            run.Sink.Record(EventTypes.AgentRunCompleted, new { runId = spec.RunId, ok = true, summary = broker.Summary, toolCalls = broker.ToolCalls, denied = broker.Denied, exitCode = run.ExitCode, outputs = hashes, elapsedMs = (long)(now - run.StartedAt).TotalMilliseconds });
            var primary = spec.RequiredOutputs.FirstOrDefault() ?? outputs.FirstOrDefault() ?? "";
            var data = new Dictionary<string, string> { ["runId"] = spec.RunId, ["output"] = primary.StartsWith(AgentRunSpec.OutFolder + "/", StringComparison.Ordinal) ? primary[(AgentRunSpec.OutFolder.Length + 1)..] : primary, ["outputPath"] = Path.Combine(spec.StagingPath, primary.Replace('/', Path.DirectorySeparatorChar)), ["projectId"] = spec.ProjectId };
            result = ExecutionResult.Ok($"{broker.Summary} → {primary} ready in staging; say \"apply the summary to {spec.ProjectSlug}\" to file it as an artifact", data);
        }

        new AgentRunStatus(spec.RunId, spec.ProjectId, spec.Task, state, broker.Summary, result.Error, ListOutputs(spec), broker.ToolCalls, run.ExitCode, run.StartedAt, now).Save(spec.StagingPath);
        run.Process?.Dispose();
        run.Cts.Dispose();
        Completed?.Invoke(run.ProposalId, result);
    }

    /// <summary>Stops one run (by proposal) or every run when <paramref name="proposalId"/> is null. Partial output stays for inspection.</summary>
    public void Stop(string? proposalId, string reason)
    {
        var runs = proposalId is null ? _active.Values.ToList() : _active.TryGetValue(proposalId, out var r) ? [r] : [];
        foreach (var run in runs) Terminate(run, reason);
    }

    // ----------------------------------------------------------------------------------------
    // apply_patch
    // ----------------------------------------------------------------------------------------

    public ExecutionResult ApplyPatch(Proposal proposal, Decision decision, IExecutionSink sink)
    {
        var t = decision.NormalizedTarget;
        var project = _registry.ById(t["projectId"]);
        if (project is null) return ExecutionResult.Fail("Project vanished.");
        var runId = t["runId"];
        var staging = Path.Combine(_root.AgentsDirectory, runId);
        var status = AgentRunStatus.Load(staging);
        if (status is null || status.State != "completed") return ExecutionResult.Fail($"Worker run {runId} did not complete; nothing to apply.");
        if (status.ProjectId != project.Id) return ExecutionResult.Fail($"Worker run {runId} belongs to a different project.");

        var source = Path.GetFullPath(Path.Combine(staging, AgentRunSpec.OutFolder, t["output"].Replace('/', Path.DirectorySeparatorChar)));
        var outRoot = Path.GetFullPath(Path.Combine(staging, AgentRunSpec.OutFolder)) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase)) return ExecutionResult.Fail("Output path escapes the run's out folder.");
        if (!File.Exists(source)) return ExecutionResult.Fail($"Output '{t["output"]}' does not exist in run {runId}.");

        var destination = t["destinationPath"];
        var content = File.ReadAllText(source);
        string? previousVersionPath = null;
        if (File.Exists(destination))
        {
            var relative = Path.GetRelativePath(project.RootPath, destination).Replace(Path.DirectorySeparatorChar, '_');
            var versionsDir = Path.Combine(ProjectLayout.VersionsDirectory(project.RootPath), "artifacts");
            Directory.CreateDirectory(versionsDir);
            var next = Directory.EnumerateFiles(versionsDir, relative + ".*").Count() + 1;
            previousVersionPath = Path.Combine(versionsDir, $"{relative}.{next}");
            File.Copy(destination, previousVersionPath, overwrite: false);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        AtomicFile.WriteAllText(destination, content);
        var sha = Sha256File(destination);
        (status with { Applied = true }).Save(staging);
        sink.Record(EventTypes.PatchApplied, new { runId, projectId = project.Id, projectSlug = project.Slug, output = t["output"], destination, sha256 = sha, previousVersionPath, proposalId = proposal.ProposalId, by = proposal.ProposedBy });
        return ExecutionResult.Ok($"Applied {t["output"]} from run {runId} to {project.Slug}/{t["destination"]}" + (previousVersionPath is null ? "" : "; previous version kept"),
            new Dictionary<string, string> { ["path"] = destination, ["sha256"] = sha });
    }

    private static IReadOnlyList<string> ListOutputs(AgentRunSpec spec)
    {
        if (!Directory.Exists(spec.OutPath)) return [];
        return Directory.EnumerateFiles(spec.OutPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(spec.StagingPath, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static string Sha256File(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
