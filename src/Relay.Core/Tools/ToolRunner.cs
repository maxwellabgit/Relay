using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Agents;
using Relay.Core.Config;
using Relay.Core.Ids;
using Relay.Core.Storage;

namespace Relay.Core.Tools;

/// <summary>What one sandboxed call of a built tool came to.</summary>
public sealed record ToolRunResult(bool Ok, string? ResultJson, string? Error, string RunId, int HostCalls, int Denied, long ElapsedMs, IReadOnlyList<BrokerEvent> Events, IReadOnlyList<string> Logs)
{
    /// <summary>The result as one line for a transcript or a feed, clipped.</summary>
    public string Summary(int max = 300)
    {
        var text = Ok ? ResultJson ?? "null" : Error ?? "failed";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

/// <summary>
/// Runs one call of a built tool in the worker sandbox: the same worker executable, host and broker
/// as agent runs, with a run folder under <c>staging\tools\runs</c> holding the source and the call as
/// read-only inputs and the result as the one output. Every host function the tool asks for is a
/// broker call checked against the tool's declared list; the wall clock, call count and byte budgets are
/// enforced here, memory and process confinement by the host. Nothing here touches the ledger: the
/// caller records the run's events on its own thread.
/// </summary>
public sealed class ToolRunner
{
    public const int DefaultCallTimeoutSeconds = 10;
    public const int MaxCallTimeoutSeconds = 60;
    private const int MaxHostCalls = 50;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private readonly DataRoot _root;
    private readonly IWorkerHost _host;
    private readonly HostFunctions _functions;
    private readonly WorkerSettings _settings;
    private readonly Func<DateTimeOffset> _clock;

    public ToolRunner(DataRoot root, IWorkerHost host, HostFunctions functions, WorkerSettings settings, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _host = host;
        _functions = functions;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IWorkerHost Host => _host;

    public Task<ToolRunResult> RunAsync(ToolPackage tool, IReadOnlyDictionary<string, string> args, string purpose, string? taskId, CancellationToken cancellationToken)
        => RunAsync(tool, args, purpose, taskId, DefaultCallTimeoutSeconds, cancellationToken);

    public async Task<ToolRunResult> RunAsync(ToolPackage tool, IReadOnlyDictionary<string, string> args, string purpose, string? taskId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var now = _clock();
        var runId = Ulid.NewUlid(now);
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, MaxCallTimeoutSeconds);
        AgentRunSpec spec;
        try { spec = Stage(tool, args, purpose, taskId, runId, timeoutSeconds, now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ToolRunResult(false, null, "Could not stage the tool run: " + ex.Message, runId, 0, 0, watch.ElapsedMilliseconds, [], []);
        }

        var broker = new WorkerBroker(spec) { HostCall = (fn, arg) => _functions.Invoke(fn, arg) };
        var logs = new List<string>();
        IWorkerProcess process;
        try { process = _host.Start(spec); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or FileNotFoundException)
        {
            return Finish(spec, broker, logs, false, null, "Could not start the worker: " + ex.Message, watch, now);
        }

        string? terminated = null;
        using (process)
        using (var wall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            wall.CancelAfter(TimeSpan.FromSeconds(spec.Limits.WallClockSeconds));
            var ct = wall.Token;
            try
            {
                await process.WriteLineAsync(spec.ToSpecMessage(), ct).ConfigureAwait(false);
                while (true)
                {
                    var line = await process.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    var turn = broker.Handle(line);
                    if (turn.Log is not null && logs.Count < 50) logs.Add(turn.Log);
                    if (turn.Reply is not null) await process.WriteLineAsync(turn.Reply, ct).ConfigureAwait(false);
                    if (turn.Finished) break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                terminated = cancellationToken.IsCancellationRequested ? "the call was cancelled" : wall.IsCancellationRequested ? $"wall clock limit of {spec.Limits.WallClockSeconds}s reached" : "the worker pipe broke: " + ex.Message;
                try { await process.WriteLineAsync(WorkerBroker.StopMessage(terminated), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); } catch (Exception) { }
                process.Kill(terminated);
            }

            // The worker exits on its own after done/failed; a grace period, then the kill switch.
            if (terminated is null)
            {
                try { await process.Exited.WaitAsync(ExitGrace).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill("did not exit after finishing"); }
            }
        }

        var tampered = spec.VerifyInputsUnchanged();
        if (terminated is not null) return Finish(spec, broker, logs, false, null, "Tool run terminated: " + terminated, watch, now);
        if (tampered.Count > 0) return Finish(spec, broker, logs, false, null, "Tool run escaped the broker: " + string.Join("; ", tampered), watch, now);
        if (broker.Failed) return Finish(spec, broker, logs, false, null, broker.Error ?? "the tool failed", watch, now);
        if (!broker.Done) return Finish(spec, broker, logs, false, null, "The worker exited without reporting a result.", watch, now);
        var resultPath = Path.Combine(spec.StagingPath, AgentRunSpec.OutFolder, "result.json");
        if (!File.Exists(resultPath)) return Finish(spec, broker, logs, false, null, "The tool finished but wrote no result.", watch, now);
        string json;
        try { json = File.ReadAllText(resultPath, Encoding.UTF8); }
        catch (IOException ex) { return Finish(spec, broker, logs, false, null, "Could not read the result: " + ex.Message, watch, now); }
        return Finish(spec, broker, logs, true, json, null, watch, now);
    }

    private ToolRunResult Finish(AgentRunSpec spec, WorkerBroker broker, List<string> logs, bool ok, string? json, string? error, Stopwatch watch, DateTimeOffset startedAt)
    {
        try
        {
            new AgentRunStatus(spec.RunId, spec.ProjectId, spec.Task, ok ? "completed" : "failed", ok ? $"ok ({json?.Length ?? 0} chars)" : null, error,
                ok ? [AgentRunSpec.OutFolder + "/result.json"] : [], broker.ToolCalls, null, startedAt, _clock()).Save(spec.StagingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new ToolRunResult(ok, json, error, spec.RunId, broker.HostCalls, broker.Denied, watch.ElapsedMilliseconds, broker.Events.ToList(), logs);
    }

    /// <summary>The run folder: the source and the call as read-only inputs (hashed, so tampering is detected), an empty out folder, run.json.</summary>
    private AgentRunSpec Stage(ToolPackage tool, IReadOnlyDictionary<string, string> args, string purpose, string? taskId, string runId, int timeoutSeconds, DateTimeOffset now)
    {
        var staging = Path.Combine(_root.ToolRunsDirectory, runId);
        Directory.CreateDirectory(Path.Combine(staging, AgentRunSpec.InputsFolder));
        Directory.CreateDirectory(Path.Combine(staging, AgentRunSpec.OutFolder));
        Directory.CreateDirectory(Path.Combine(staging, "tmp"));
        var call = JsonSerializer.Serialize(new { tool = tool.Name, args = args.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), timeoutSeconds }, RelayJson.Indented);
        var inputs = new List<AgentInput> { WriteInput(staging, "inputs/tool.js", tool.Source, "tool:" + tool.Name), WriteInput(staging, "inputs/call.json", call, "call") };
        var spec = new AgentRunSpec
        {
            RunId = runId,
            ProposalId = taskId ?? "tool",
            TurnId = taskId ?? "",
            ProjectId = "",
            ProjectSlug = "tool:" + tool.Name,
            Task = "tool",
            Objective = purpose,
            Inputs = inputs,
            ToolAllowlist = ["read_file", "write_file", "host"],
            HostAllow = tool.HostFunctionNames,
            Limits = new AgentLimits(timeoutSeconds + 5, 1024 * 1024, 256 * 1024, 3 + MaxHostCalls + 1, _settings.MemoryMb * 1024L * 1024L),
            StagingPath = staging,
            RequiredOutputs = [AgentRunSpec.OutFolder + "/result.json"],
            CreatedAt = now,
        };
        AtomicFile.WriteAllText(spec.SpecPath, JsonSerializer.Serialize(spec, RelayJson.Indented));
        return spec;
    }

    private static AgentInput WriteInput(string staging, string relative, string text, string source)
    {
        var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
        var bytes = new UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(path, bytes);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return new AgentInput(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, source);
    }
}
