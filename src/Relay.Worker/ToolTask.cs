using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Relay.Worker;

/// <summary>
/// Runs one call of a tool Relay built for itself (docs/09, slice 6). The tool is a JavaScript
/// module under <c>inputs/tool.js</c> that defines <c>function run(args)</c>; the call's arguments
/// are <c>inputs/call.json</c>; the result, as JSON, goes to <c>out/result.json</c>. The script
/// runs in Jint with a statement cap, a wall clock, a recursion limit and a memory limit, and
/// sees nothing of the machine: no file system, no network, no process. The only way out is
/// <c>relay.*</c>, whose every call goes through the broker as a <c>host</c> tool call the run's
/// declared host functions must allow. The engine runs on its own thread because a host call from
/// inside JavaScript has to block until the broker answers, and the broker conversation is driven
/// by the worker's main thread.
/// </summary>
public static class ToolTask
{
    public const string SourcePath = "inputs/tool.js";
    public const string CallPath = "inputs/call.json";
    public const string ResultPath = "out/result.json";
    public const int MaxStatements = 2_000_000;
    public const int MaxRecursion = 64;
    public const long MaxMemoryBytes = 64L * 1024 * 1024;
    public const int MaxResultChars = 20_000;
    public const int MaxHostCalls = 50;

    /// <summary>The bridge the script sees. Each function is one broker call; the broker answers with a string or denies, and a denial is a catchable Error.</summary>
    private const string Prelude = """
        var relay = Object.freeze({
          now: function () { return __host("time.now", ""); },
          zone: function (zoneId) { return JSON.parse(__host("time.zone", String(zoneId))); },
          log: function (text) { __host("log", String(text)); return undefined; }
        });
        var console = Object.freeze({
          log: function () { __host("log", Array.prototype.slice.call(arguments).join(" ")); return undefined; }
        });
        """;

    public static async Task<(string Summary, IReadOnlyList<string> Outputs)> RunAsync(RunSpecMessage spec, BrokerClient broker)
    {
        var source = await broker.CallAsync("read_file", ("path", SourcePath)).ConfigureAwait(false);
        var callJson = await broker.CallAsync("read_file", ("path", CallPath)).ConfigureAwait(false);
        JsonObject call;
        try { call = JsonNode.Parse(callJson)?.AsObject() ?? throw new InvalidDataException("call.json is not an object"); }
        catch (JsonException ex) { throw new InvalidDataException("call.json is not valid JSON: " + ex.Message); }
        var argsJson = (call["args"] ?? new JsonObject()).ToJsonString();
        var seconds = Math.Clamp(call["timeoutSeconds"]?.GetValue<int>() ?? 10, 1, 60);

        var result = await Task.Run(() => Evaluate(source, argsJson, seconds, broker)).ConfigureAwait(false);
        if (result.Length > MaxResultChars) throw new InvalidDataException($"Tool failed: the result is {result.Length} characters; at most {MaxResultChars} are allowed.");
        await broker.CallAsync("write_file", ("path", ResultPath), ("text", result)).ConfigureAwait(false);
        return ($"ok ({result.Length} chars)", [ResultPath]);
    }

    /// <summary>Runs the script to completion on the calling thread; host calls block this thread on the broker.</summary>
    private static string Evaluate(string source, string argsJson, int seconds, BrokerClient broker)
    {
        var hostCalls = 0;
        Engine? engine = null;
        try
        {
            engine = new Engine(options => options
                .Strict()
                .TimeoutInterval(TimeSpan.FromSeconds(seconds))
                .MaxStatements(MaxStatements)
                .LimitRecursion(MaxRecursion)
                .LimitMemory(MaxMemoryBytes));
            engine.SetValue("__host", new Func<string, string, string>((fn, arg) =>
            {
                if (fn == "log")
                {
                    broker.LogAsync(arg.Length > 300 ? arg[..300] : arg).GetAwaiter().GetResult();
                    return "";
                }
                if (++hostCalls > MaxHostCalls) throw new JavaScriptException(engine!.Intrinsics.Error, $"more than {MaxHostCalls} host calls in one run");
                try { return broker.CallAsync("host", ("fn", fn), ("arg", arg)).GetAwaiter().GetResult(); }
                catch (BrokerDeniedException ex) { throw new JavaScriptException(engine!.Intrinsics.Error, ex.Message); }
            }));
            engine.Execute(Prelude);
            engine.Execute(source);
            var run = engine.GetValue("run");
            if (run.IsUndefined() || !run.IsObject()) throw new InvalidDataException("Tool failed: the source does not define function run(args).");
            engine.SetValue("__args", new Jint.Native.Json.JsonParser(engine).Parse(argsJson));
            var value = engine.Evaluate("JSON.stringify(run(__args))");
            return value.IsString() ? value.AsString() : "null";
        }
        catch (JavaScriptException ex) { throw new InvalidDataException("Tool failed: " + ex.Message + LocationOf(ex)); }
        catch (StatementsCountOverflowException) { throw new InvalidDataException($"Tool failed: ran more than {MaxStatements} statements (an endless loop?)."); }
        catch (TimeoutException) { throw new InvalidDataException($"Tool failed: did not finish within {seconds}s."); }
        catch (RecursionDepthOverflowException) { throw new InvalidDataException($"Tool failed: recursion deeper than {MaxRecursion}."); }
        catch (MemoryLimitExceededException) { throw new InvalidDataException("Tool failed: used more memory than allowed."); }
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or EndOfStreamException or IOException or BrokerDeniedException))
        {
            // Parse errors and anything else the engine or the CLR bridge throws: the message is what the builder feeds back.
            throw new InvalidDataException("Tool failed: " + ex.Message);
        }
        finally
        {
            engine?.Dispose();
        }
    }

    private static string LocationOf(JavaScriptException ex)
        => ex.Location.Start.Line > 0 ? $" (line {ex.Location.Start.Line})" : "";
}
