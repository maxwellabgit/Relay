using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using Relay.Core.Cases;
using Relay.Core.Ids;
using Relay.Core.Tools;

namespace Relay.DevHarness;

/// <summary>In-process Jint runner matching Relay.Worker ToolTask package shape (no worker process).</summary>
internal sealed class InProcessJintToolRunner : IToolPackageRunner
{
    private const string Prelude = """
        var __now = function () { return __host("time.now", ""); };
        var __zone = function (zoneId) { return JSON.parse(__host("time.zone", String(zoneId))); };
        var relay = Object.freeze({
          now: __now,
          zone: __zone,
          time: Object.freeze({ now: __now, zone: __zone }),
          log: function (text) { __host("log", String(text)); return undefined; }
        });
        """;

    private readonly HostFunctions _functions;
    private readonly Func<DateTimeOffset> _clock;

    public InProcessJintToolRunner(HostFunctions functions, Func<DateTimeOffset> clock)
    {
        _functions = functions;
        _clock = clock;
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
                if (fn == "log") return "";
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
            engine.SetValue("__args", new Jint.Native.Json.JsonParser(engine).Parse(argsJson));
            var value = engine.Evaluate("JSON.stringify(run(__args))");
            var json = value.IsString() ? value.AsString() : "null";
            return Task.FromResult(new ToolRunResult(true, json, null, runId, hostCalls, denied, watch.ElapsedMilliseconds, [], []));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolRunResult(false, null, "Tool failed: " + ex.Message, runId, hostCalls, denied, watch.ElapsedMilliseconds, [], []));
        }
    }
}
