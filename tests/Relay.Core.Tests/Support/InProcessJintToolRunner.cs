using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Relay.Core.Ids;
using Relay.Core.Tools;
using Relay.Core.Cases;

namespace Relay.Core.Tests.Support;

/// <summary>
/// In-process Jint runner that executes the same package shape as Relay.Worker ToolTask,
/// without spawning a worker process. Suitable for Linux Core.Tests / harness.
/// </summary>
public sealed class InProcessJintToolRunner : IToolPackageRunner
{
    public const string Prelude = """
        var __now = function () { return __host("time.now", ""); };
        var __zone = function (zoneId) { return JSON.parse(__host("time.zone", String(zoneId))); };
        var relay = Object.freeze({
          now: __now,
          zone: __zone,
          time: Object.freeze({ now: __now, zone: __zone }),
          log: function (text) { __host("log", String(text)); return undefined; }
        });
        var console = Object.freeze({
          log: function () { __host("log", Array.prototype.slice.call(arguments).join(" ")); return undefined; }
        });
        """;

    private readonly HostFunctions _functions;
    private readonly Func<DateTimeOffset> _clock;

    public InProcessJintToolRunner(HostFunctions? functions = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _functions = functions ?? new HostFunctions(_clock);
    }

    public Task<ToolRunResult> RunAsync(
        ToolPackage tool,
        IReadOnlyDictionary<string, string> args,
        string purpose,
        string? taskId,
        int timeoutSeconds,
        DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var runId = Ulid.NewUlid(_clock());
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, ToolRunner.MaxCallTimeoutSeconds);
        var hostCalls = 0;
        var denied = 0;
        var logs = new List<string>();
        var allow = tool.HostFunctionNames.ToHashSet(StringComparer.Ordinal);
        var present = at ?? _clock();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var argsJson = JsonSerializer.Serialize(args);
            using var engine = new Engine(options => options
                .Strict()
                .TimeoutInterval(TimeSpan.FromSeconds(timeoutSeconds))
                .MaxStatements(2_000_000)
                .LimitRecursion(64)
                .LimitMemory(64L * 1024 * 1024));

            engine.SetValue("__host", new Func<string, string, string>((fn, arg) =>
            {
                if (fn == "log")
                {
                    logs.Add(arg.Length > 300 ? arg[..300] : arg);
                    return "";
                }
                if (!allow.Contains(fn))
                {
                    denied++;
                    throw new JavaScriptException(engine.Intrinsics.Error, $"host function '{fn}' is not declared by this tool");
                }
                if (++hostCalls > 50)
                    throw new JavaScriptException(engine.Intrinsics.Error, "more than 50 host calls in one run");
                var (ok, result) = _functions.Invoke(fn, arg, present);
                if (!ok) throw new JavaScriptException(engine.Intrinsics.Error, result);
                return result;
            }));
            engine.Execute(Prelude);
            engine.Execute(tool.Source);
            var run = engine.GetValue("run");
            if (run.IsUndefined() || !run.IsObject())
                return Task.FromResult(Fail(runId, watch, hostCalls, denied, logs, "the source does not define function run(args)"));

            engine.SetValue("__args", new Jint.Native.Json.JsonParser(engine).Parse(argsJson));
            var value = engine.Evaluate("JSON.stringify(run(__args))");
            var json = value.IsString() ? value.AsString() : "null";
            if (json.Length > 20_000)
                return Task.FromResult(Fail(runId, watch, hostCalls, denied, logs, "result too large"));

            return Task.FromResult(new ToolRunResult(true, json, null, runId, hostCalls, denied, watch.ElapsedMilliseconds, [], logs));
        }
        catch (JavaScriptException ex)
        {
            return Task.FromResult(Fail(runId, watch, hostCalls, denied, logs, "Tool failed: " + ex.Message));
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(runId, watch, hostCalls, denied, logs, "Tool failed: " + ex.Message));
        }
    }

    private static ToolRunResult Fail(string runId, Stopwatch watch, int hostCalls, int denied, List<string> logs, string error)
        => new(false, null, error, runId, hostCalls, denied, watch.ElapsedMilliseconds, [], logs);
}
