using Relay.Core.Config;
using Relay.Core.Mind;
using Relay.Core.SelfChange;
using Relay.Core.Tools;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>
/// Capability building (docs/09 slice 6) below the coordinator: the sandbox a built tool runs in, the package
/// contract, the builder's draft → test → retry, and promotion as one revertible change set.
/// </summary>
public class ToolBuildingTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly FixedClock _clock = new(Harness.T0);
    private readonly InProcessWorkerHost _host = new();

    public ToolBuildingTests() => _tmp.Root.EnsureLayout(_clock);

    private ToolRunner Runner() => new(_tmp.Root, _host, new HostFunctions(() => _clock.UtcNow), new WorkerSettings(), () => _clock.UtcNow);
    private ToolStore Store() => new(_tmp.Root, new ChangeSetStore(_tmp.Root));

    private static ToolPackage Package(string source, string[]? hostFunctions = null, string name = "probe", params ToolTest[] tests) => new()
    {
        Name = name,
        Description = "a probe",
        Arguments = [new ToolArgument("zone", "IANA zone id", Required: false)],
        HostFunctionNames = hostFunctions ?? [],
        Source = source,
        Tests = tests.Length == 0 ? [new ToolTest(new Dictionary<string, string>())] : tests,
    };

    private static Dictionary<string, string> Args(params (string Key, string Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

    [Fact]
    public async Task AWorldClockToolRunsInTheSandboxAndAsksTheMachineOnlyThroughItsDeclaredHostFunction()
    {
        var package = Package(ScriptedDrafter.WorldClockSource, ["time.zone"], "world_clock");
        var run = await Runner().RunAsync(package, Args(("zone", "Asia/Tokyo")), "probe", null, CancellationToken.None);

        Assert.True(run.Ok, run.Error);
        Assert.Equal(1, run.HostCalls);
        Assert.Equal(0, run.Denied);
        var result = System.Text.Json.JsonDocument.Parse(run.ResultJson!).RootElement;
        Assert.Equal("Asia/Tokyo", result.GetProperty("zone").GetString());
        Assert.Equal("21:00", result.GetProperty("time").GetString());      // T0 is 12:00Z; Tokyo is +09:00 all year
        Assert.Equal("2026-09-04", result.GetProperty("date").GetString());
        Assert.Equal("+09:00", result.GetProperty("offset").GetString());
        Assert.Equal("Friday", result.GetProperty("weekday").GetString());

        // The run folder holds the source and the call as read-only inputs and the result as the one output; the spec leaks no absolute path.
        var spec = Assert.Single(_host.Started);
        Assert.Equal("tool", spec.Task);
        Assert.Equal(new[] { "time.zone" }, spec.HostAllow);
        Assert.Equal(new[] { "read_file", "write_file", "host" }, spec.ToolAllowlist);
        Assert.DoesNotContain(":\\", spec.ToSpecMessage());
        Assert.True(File.Exists(Path.Combine(spec.StagingPath, "out", "result.json")));
        Assert.StartsWith(_tmp.Root.ToolRunsDirectory, spec.StagingPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUndeclaredHostFunctionIsDeniedByTheBrokerAndTheScriptSeesACatchableError()
    {
        var uncaught = Package("function run(args) { return { now: relay.now() }; }");
        var run = await Runner().RunAsync(uncaught, Args(), "probe", null, CancellationToken.None);
        Assert.False(run.Ok);
        Assert.Equal(1, run.Denied);
        Assert.Equal(0, run.HostCalls);
        Assert.Contains("not declared by this tool", run.Error);

        var caught = Package("function run(args) { try { relay.now(); return { escaped: true }; } catch (e) { return { denied: String(e.message) }; } }");
        run = await Runner().RunAsync(caught, Args(), "probe", null, CancellationToken.None);
        Assert.True(run.Ok, run.Error);
        Assert.Contains("not declared by this tool", run.ResultJson);
        Assert.Contains("\"denied\"", run.ResultJson);
        Assert.Contains(run.Events, e => e.Type == Relay.Core.Ledger.EventTypes.AgentRunToolDenied);

        var unknownZone = Package("function run(args) { return relay.zone(args.zone); }", ["time.zone"]);
        run = await Runner().RunAsync(unknownZone, Args(("zone", "Mars/Olympus")), "probe", null, CancellationToken.None);
        Assert.False(run.Ok);
        Assert.Contains("unknown time zone", run.Error);
    }

    [Fact]
    public async Task TheSandboxHasNoWayToTheMachineBesidesRelay()
    {
        const string probe = """
            function run(args) {
              var out = {};
              var names = ["require", "process", "fetch", "XMLHttpRequest", "setTimeout", "setInterval", "importScripts", "System", "window", "document", "Deno", "Bun", "WebAssembly"];
              for (var i = 0; i < names.length; i++) out[names[i]] = typeof globalThis[names[i]];
              try { out.clr = typeof (new Function("return System.IO.File"))(); } catch (e) { out.clr = "error: " + e.name; }
              try { out.eval_process = typeof eval("process"); } catch (e) { out.eval_process = "error: " + e.name; }
              try { relay.now = function () { return "fake"; }; out.relayFrozen = false; } catch (e) { out.relayFrozen = true; }
              out.relayKeys = Object.keys(relay).join(",");
              return out;
            }
            """;
        var run = await Runner().RunAsync(Package(probe), Args(), "probe", null, CancellationToken.None);
        Assert.True(run.Ok, run.Error);
        var result = System.Text.Json.JsonDocument.Parse(run.ResultJson!).RootElement;
        foreach (var name in new[] { "require", "process", "fetch", "XMLHttpRequest", "setTimeout", "setInterval", "importScripts", "System", "window", "document", "Deno", "Bun", "WebAssembly" })
            Assert.Equal("undefined", result.GetProperty(name).GetString());
        Assert.StartsWith("error: ReferenceError", result.GetProperty("clr").GetString());
        Assert.StartsWith("error: ReferenceError", result.GetProperty("eval_process").GetString());
        Assert.True(result.GetProperty("relayFrozen").GetBoolean()); // strict mode: writing to the frozen bridge throws
        Assert.Equal("now,zone,log", result.GetProperty("relayKeys").GetString());
        Assert.Equal(0, run.HostCalls);
    }

    [Fact]
    public async Task RunawayScriptsAreEndedByTheCaps()
    {
        var runner = Runner();

        var endless = await runner.RunAsync(Package("function run(args) { while (true) {} }"), Args(), "probe", null, timeoutSeconds: 2, CancellationToken.None);
        Assert.False(endless.Ok);
        Assert.Contains("Tool failed", endless.Error);
        Assert.True(endless.Error!.Contains("statements") || endless.Error.Contains("did not finish"), endless.Error);

        var recursion = await runner.RunAsync(Package("function f(n) { return f(n + 1); } function run(args) { return f(0); }"), Args(), "probe", null, CancellationToken.None);
        Assert.False(recursion.Ok);
        Assert.Contains("recursion", recursion.Error);

        // Each iteration allocates a fresh ~200 KB string (slice, not a lazy concatenation), so the 64 MB allocation cap ends it within a few hundred iterations.
        var memory = await runner.RunAsync(Package("function run(args) { var a = []; var chunk = 'x'.repeat(100000); for (;;) { a.push(chunk.slice(1 + (a.length % 5))); } }"), Args(), "probe", null, timeoutSeconds: 3, CancellationToken.None);
        Assert.False(memory.Ok);
        Assert.Contains("Tool failed", memory.Error);

        var huge = await runner.RunAsync(Package("function run(args) { return 'x'.repeat(30000); }"), Args(), "probe", null, CancellationToken.None);
        Assert.False(huge.Ok);
        Assert.Contains("characters", huge.Error);

        var noRun = await runner.RunAsync(Package("var x = 1;"), Args(), "probe", null, CancellationToken.None);
        Assert.False(noRun.Ok);
        Assert.Contains("does not define function run", noRun.Error);

        var syntax = await runner.RunAsync(Package("function run(args) { return { ; }"), Args(), "probe", null, CancellationToken.None);
        Assert.False(syntax.Ok);
        Assert.Contains("Tool failed", syntax.Error);

        var hostCalls = await runner.RunAsync(Package("function run(args) { var n = 0; for (var i = 0; i < 60; i++) { relay.now(); n++; } return n; }", ["time.now"]), Args(), "probe", null, CancellationToken.None);
        Assert.False(hostCalls.Ok);
        Assert.Contains("host calls", hostCalls.Error);
    }

    [Fact]
    public async Task LogsReachTheRunAndNotTheResult()
    {
        var run = await Runner().RunAsync(Package("function run(args) { relay.log('starting'); console.log('half', 'way'); return { ok: true }; }"), Args(), "probe", null, CancellationToken.None);
        Assert.True(run.Ok, run.Error);
        Assert.Equal(new[] { "starting", "half way" }, run.Logs);
        Assert.Equal("{\"ok\":true}", run.ResultJson);
        Assert.Equal(0, run.HostCalls); // logging is not a host function
    }

    [Fact]
    public void PackageValidationCatchesWhatMustNeverRun()
    {
        var reserved = new[] { "search", "list_projects" };
        var bad = new ToolPackage
        {
            Name = "Search",
            Description = "",
            Arguments = [new ToolArgument("Zone Id", ""), new ToolArgument("x", "x"), new ToolArgument("x", "x again")],
            HostFunctionNames = ["fs.read", "time.zone"],
            Source = "var noRunHere = 1;",
            Tests = [new ToolTest(new Dictionary<string, string> { ["undeclared"] = "1" }, Matches: "(")],
        };
        var problems = bad.Validate(reserved);
        Assert.Contains(problems, p => p.Contains("snake_case", StringComparison.Ordinal) && p.Contains("name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("description is required", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("argument 'Zone Id'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("argument names must be unique", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("host function 'fs.read' does not exist", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("must define function run", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'undeclared'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("invalid regular expression", StringComparison.Ordinal));

        var taken = new ToolPackage { Name = "search", Description = "d", Source = "function run(a) { return 1; }", Tests = [new ToolTest(new Dictionary<string, string>())] };
        Assert.Contains(taken.Validate(reserved), p => p.Contains("already exists", StringComparison.Ordinal));

        var noTests = new ToolPackage { Name = "fine", Description = "d", Source = "function run(a) { return 1; }" };
        Assert.Equal(new[] { "at least one test is required" }, noTests.Validate(reserved));

        var good = new ToolPackage { Name = "fine", Description = "d", Source = "function run(a) { return 1; }", Tests = [new ToolTest(new Dictionary<string, string>())] };
        Assert.Empty(good.Validate(reserved));
        Assert.False(good.Tested);

        Assert.Null(ToolPackage.Check(new ToolTest(new Dictionary<string, string>(), Keys: ["a"], Contains: "\"a\":1", Matches: "\\d"), "{\"a\":1}"));
        Assert.Contains("lacks key(s) b", ToolPackage.Check(new ToolTest(new Dictionary<string, string>(), Keys: ["b"]), "{\"a\":1}"));
        Assert.Contains("not an object", ToolPackage.Check(new ToolTest(new Dictionary<string, string>(), Keys: ["b"]), "42"));
        Assert.Contains("does not contain", ToolPackage.Check(new ToolTest(new Dictionary<string, string>(), Contains: "zzz"), "{\"a\":1}"));
    }

    [Fact]
    public async Task TheBuilderDraftsTestsInTheSandboxAndRetriesOnceWithTheFailureFedBack()
    {
        // First draft returns only the zone, so the test that wants a time fails; the retry gets the failure and the failed source verbatim.
        var drafter = new ScriptedDrafter()
            .Reply(ScriptedDrafter.WorldClock(source: "function run(args) { var z = relay.zone(args.zone); return { zone: z.zone }; }"))
            .Reply(ScriptedDrafter.WorldClock());
        var store = Store();
        var builder = new ToolBuilder(store, Runner(), () => drafter, () => _clock.UtcNow);
        Assert.True(builder.CanDraft);
        Assert.Equal("drafter:scripted", builder.DrafterName);

        var stages = new List<BuildProgress>();
        var move = new BuildMove("world_clock", "The user keeps asking for the time in other cities and no tool gives it.", "an IANA zone id", "local time, date and weekday");
        var outcome = await builder.BuildAsync(move, "what time is it in London?", "task-1", p => { stages.Add(p); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(new[] { "drafted", "failed", "drafted", "tested" }, stages.Select(s => s.Stage));
        Assert.Equal(new[] { 1, 1, 2, 2 }, stages.Select(s => s.Attempt));
        Assert.Contains("lacks key(s) time, date", stages[1].Detail);
        Assert.Equal("1 of 1 test(s) passed in the sandbox", outcome.Summary);
        Assert.Single(outcome.Tests);

        Assert.Equal(2, drafter.Calls);
        var retry = drafter.Requests[1];
        Assert.Equal(BuildPrompt.Schema, retry.JsonSchema);
        Assert.Contains("Your previous draft failed", retry.Messages[1].Content);
        Assert.Contains("lacks key(s) time, date", retry.Messages[1].Content);
        Assert.Contains("return { zone: z.zone }", retry.Messages[1].Content);
        Assert.Contains("Tool name: world_clock", retry.Messages[1].Content);
        Assert.Contains("what time is it in London?", retry.Messages[1].Content);
        Assert.Contains("relay.zone(zoneId: string): object", retry.Messages[0].Content);

        var draft = store.Draft("world_clock")!;
        Assert.True(draft.Tested);
        Assert.Equal(draft.SourceSha256, draft.TestedSha256);
        Assert.Equal("drafter:scripted", draft.BuiltBy);
        Assert.Equal("task-1", draft.TaskId);
        Assert.Equal(move.Justification, draft.Justification);
        Assert.Equal(new[] { "time.zone" }, draft.HostFunctionNames);
        Assert.False(store.IsPromoted("world_clock"));
        Assert.Equal(2, _host.Started.Count(s => s.Task == "tool")); // one test per draft, two drafts; nothing was promoted or run otherwise
    }

    [Fact]
    public async Task ABuildThatFailsTwiceStaysInStagingWithTheLastFailure()
    {
        var drafter = new ScriptedDrafter()
            .Reply("this is not json")
            .Reply(ScriptedDrafter.WorldClock(source: "function run(args) { return {"));
        var builder = new ToolBuilder(Store(), Runner(), () => drafter, () => _clock.UtcNow);
        var stages = new List<BuildProgress>();
        var outcome = await builder.BuildAsync(new BuildMove("world_clock", "why", "", ""), "ask", null, p => { stages.Add(p); return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(new[] { "failed", "drafted", "failed" }, stages.Select(s => s.Stage));
        Assert.Contains("did not follow the package contract", stages[0].Detail);
        Assert.Contains("could not be built in 2 attempts", outcome.Summary);
        Assert.Contains("Tool failed", outcome.Summary);
        Assert.Contains("did not follow the package contract", drafter.Requests[1].Messages[1].Content);
    }

    [Fact]
    public async Task PromotionNeedsATestedDraftIsOneChangeSetAndRevertingRemovesTheTool()
    {
        var changes = new ChangeSetStore(_tmp.Root);
        var store = new ToolStore(_tmp.Root, changes);
        var builder = new ToolBuilder(store, Runner(), () => null, () => _clock.UtcNow);
        Assert.False(builder.CanDraft);

        var untested = new ToolPackage
        {
            Name = "world_clock", Description = "d", Arguments = [new ToolArgument("zone", "IANA zone id")], HostFunctionNames = ["time.zone"], Source = ScriptedDrafter.WorldClockSource,
            Tests = [new ToolTest(new Dictionary<string, string> { ["zone"] = "Europe/London" }, Keys: ["time"], Contains: "Europe/London")],
        };
        store.SaveDraft(untested);
        Assert.Contains("has not passed its tests", store.Promote("world_clock", "why", _clock.UtcNow, null, null).Error);
        Assert.Contains("no draft tool named 'other'", store.Promote("other", "why", _clock.UtcNow, null, null).Error);

        var report = await builder.TestAsync(untested, null, CancellationToken.None);
        Assert.True(report.Passed, report.Summary);
        store.SaveDraft(report.Package);
        Assert.True(store.Draft("world_clock")!.Tested);

        var promoted = store.Promote("world_clock", "Built for the London question", _clock.UtcNow, "task-1", "prop-1");
        Assert.True(promoted.Ok, promoted.Error);
        Assert.Equal(ChangeKinds.Tool, promoted.ChangeSet!.Kind);
        Assert.Null(promoted.ChangeSet.Before);
        Assert.True(store.IsPromoted("world_clock"));
        Assert.Null(store.Draft("world_clock"));                    // the draft moved
        Assert.Contains("world_clock", store.ReservedNames());
        Assert.Contains(store.Descriptors(), d => d.Name == "world_clock" && d.Arguments.SequenceEqual(["zone"]));
        Assert.NotNull(store.Promoted("world_clock")!.PromotedAt);
        Assert.Contains("no draft tool named 'world_clock'", store.Promote("world_clock", "again", _clock.UtcNow, null, null).Error ?? "");
        // A fresh draft under a promoted name is refused at promotion (and by validation before it), so a build cannot replace a tool behind the user's back.
        store.SaveDraft(report.Package);
        Assert.Contains("already exists", store.Promote("world_clock", "again", _clock.UtcNow, null, null).Error ?? "");
        File.Delete(store.DraftPath("world_clock"));

        // A promoted tool runs exactly like a draft; the run is a sandbox run like any other.
        var run = await Runner().RunAsync(store.Promoted("world_clock")!, new Dictionary<string, string> { ["zone"] = "Europe/London" }, "use", "task-2", CancellationToken.None);
        Assert.True(run.Ok, run.Error);
        Assert.Contains("\"time\":\"13:00\"", run.ResultJson); // BST in September

        var reverted = changes.Revert(promoted.ChangeSet.ChangeSetId, "not wanted", _clock.UtcNow);
        Assert.True(reverted.Ok, reverted.Error);
        Assert.False(store.IsPromoted("world_clock"));
        Assert.Empty(store.Descriptors());
        Assert.False(File.Exists(store.PromotedPath("world_clock")));
    }

    public void Dispose() => _tmp.Dispose();
}
