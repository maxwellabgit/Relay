using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Tools;
using Relay.Core.Workflows;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 5: generalize → draft world_clock → promote → reuse for Kathmandu/London;
/// promote → use → revert restores absence; fixture workflows with wait/resume.
/// </summary>
public class Slice5ToolWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    private ToolServices ToolServices() => new()
    {
        Runner = new InProcessJintToolRunner(new HostFunctions(() => _clock.UtcNow), () => _clock.UtcNow),
        Drafter = new WorldClockToolDrafter(),
        Clock = () => _clock.UtcNow,
    };

    [Fact]
    public async Task Tokyo_builds_world_clock_then_Kathmandu_and_London_reuse_without_rebuild()
    {
        _tmp.Root.EnsureLayout(_clock);
        var tools = ToolServices();
        using var diagnostics = Diag("s5-world-clock");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new WorldClockToolMind(), diagnostics, tools: tools);
        Assert.True(runtime.ToolBuildAvailable);

        // --- Tokyo: build + promote + answer ---
        var tokyo = runtime.StartDirectCase(WorldClockToolMind.TokyoAsk, CaseKind.Answer);
        var waiting = await runtime.RunUntilIdleAsync(tokyo.Id);
        Assert.Equal(CaseStatus.Waiting, waiting.Status);

        var promote = runtime.GetPendingApproval(tokyo.Id)!;
        Assert.Equal(ToolCapabilities.PromoteTool, promote.Capability);
        Assert.Equal("world_clock", promote.Arguments["name"].GetString());
        Assert.True(promote.Arguments.ContainsKey("manifest"));
        // Draft tested with counterexamples, not yet promoted.
        Assert.False(runtime.Tools!.Tools.IsPromoted("world_clock"));
        Assert.NotNull(runtime.Tools.Tools.Draft("world_clock"));
        Assert.True(runtime.Tools.Tools.Draft("world_clock")!.Tested);
        var draftTests = runtime.Tools.Tools.Draft("world_clock")!.Tests;
        Assert.Contains(draftTests, t => t.Args.Values.Any(v => v.Contains("London", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(draftTests, t => t.Args.Values.Any(v => v.Contains("Kathmandu", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(draftTests, t => t.Args.Values.Any(v => v.Contains("Tokyo", StringComparison.OrdinalIgnoreCase)));

        runtime.ApproveOperation(promote.OperationId, promote.CanonicalHash(), runtime.GetCase(tokyo.Id)!.Version);
        runtime.ExecuteOperation(promote.OperationId);
        Assert.True(runtime.Tools.Tools.IsPromoted("world_clock"));

        var answered = await runtime.RunUntilIdleAsync(tokyo.Id);
        Assert.Equal(CaseStatus.Completed, answered.Status);
        Assert.Contains("21:00", answered.Result ?? ""); // Tokyo = T0+9h
        Assert.Contains("Tokyo", answered.Result ?? "", StringComparison.OrdinalIgnoreCase);

        // --- Kathmandu: reuse, no rebuild ---
        var ktm = runtime.StartDirectCase("What time is it in Kathmandu?", CaseKind.Answer);
        var ktmDone = await runtime.RunUntilIdleAsync(ktm.Id);
        Assert.Equal(CaseStatus.Completed, ktmDone.Status);
        Assert.Contains("Kathmandu", ktmDone.Result ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(runtime.GetPendingApproval(ktm.Id)); // no second promote
        Assert.DoesNotContain(runtime.Cases.LoadEvents(ktm.Id), e =>
            e.Type == CaseEventTypes.ToolResult
            && e.Payload.TryGetProperty("tool", out var t)
            && t.GetString() == "build");

        // --- London: reuse ---
        var lon = runtime.StartDirectCase("What time is it in London?", CaseKind.Answer);
        var lonDone = await runtime.RunUntilIdleAsync(lon.Id);
        Assert.Equal(CaseStatus.Completed, lonDone.Status);
        Assert.Contains("London", lonDone.Result ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Single(runtime.Tools.Changes.All(), c => c.Kind == "tool" && !c.Reverted);

        // --- promote → use → revert restores absence ---
        Assert.True(runtime.Tools.Tools.IsPromoted("world_clock"));
        var changeSetId = runtime.Tools.Changes.All().Single(c => c.Kind == "tool" && !c.Reverted).ChangeSetId;
        var revertMind = new ScriptedRevertMind(changeSetId);
        using var diagnostics2 = Diag("s5-revert");
        using var runtime2 = CaseRuntime.Open(_tmp.Root, _clock, revertMind, diagnostics2, tools: tools);
        var rev = runtime2.StartDirectCase("Revert world_clock", CaseKind.Improve);
        await runtime2.RunUntilIdleAsync(rev.Id);
        var revOp = runtime2.GetPendingApproval(rev.Id)!;
        Assert.Equal(ToolCapabilities.RevertTool, revOp.Capability);
        runtime2.ApproveOperation(revOp.OperationId, revOp.CanonicalHash(), runtime2.GetCase(rev.Id)!.Version);
        runtime2.ExecuteOperation(revOp.OperationId);
        Assert.False(runtime2.Tools!.Tools.IsPromoted("world_clock"));
        Assert.False(File.Exists(runtime2.Tools.Tools.PromotedPath("world_clock")));
    }

    [Fact]
    public void Workflow_fixture_evaluation_with_wait_resume_and_permission_union()
    {
        _tmp.Root.EnsureLayout(_clock);
        var changes = new Relay.Core.SelfChange.ChangeSetStore(_tmp.Root);
        var store = new WorkflowStore(_tmp.Root, changes);
        var builder = new WorkflowBuilder(store, () => _clock.UtcNow);

        var draft = new WorkflowDefinition
        {
            Name = "zone_then_say",
            Description = "Wait for a zone, set a greeting, say it.",
            Inputs = [new WorkflowTypedSlot("prefix", "string")],
            Outputs = [new WorkflowTypedSlot("message", "string")],
            Steps =
            [
                new WorkflowStep("wait", new Dictionary<string, string> { ["key"] = "zone" }, Id: "w1", Out: "zone"),
                new WorkflowStep("set", new Dictionary<string, string>
                {
                    ["name"] = "message",
                    ["value"] = "${input.prefix} ${zone}",
                }, Id: "s1", Out: "message"),
                new WorkflowStep("say", new Dictionary<string, string> { ["text"] = "${message}" }, Id: "say1"),
            ],
            Fixtures =
            [
                new WorkflowFixture(
                    "london",
                    new Dictionary<string, string> { ["prefix"] = "Hello" },
                    Expected: new Dictionary<string, string> { ["message"] = "Hello Europe/London" },
                    Resume: new Dictionary<string, string> { ["zone"] = "Europe/London" }),
                new WorkflowFixture(
                    "kathmandu",
                    new Dictionary<string, string> { ["prefix"] = "Namaste" },
                    Expected: new Dictionary<string, string> { ["message"] = "Namaste Asia/Kathmandu" },
                    Resume: new Dictionary<string, string> { ["zone"] = "Asia/Kathmandu" }),
            ],
            BuiltBy = "test",
            DraftedAt = _clock.UtcNow,
        };

        Assert.Contains("wait", draft.PermissionUnion);
        Assert.Contains("local", draft.PermissionUnion);

        Assert.False(store.Promote("zone_then_say", "why", _clock.UtcNow, null, null).Ok);

        var report = builder.TestWithFixtures(draft);
        Assert.True(report.Passed, report.Summary);
        Assert.True(report.Package.Tested);

        var promoted = store.Promote("zone_then_say", "fixture-evaluated", _clock.UtcNow, "case-1", "op-1");
        Assert.True(promoted.Ok, promoted.Error);
        Assert.True(store.IsPromoted("zone_then_say"));

        var live = WorkflowBuilder.Run(
            store.Promoted("zone_then_say")!,
            new Dictionary<string, string> { ["prefix"] = "Hi" },
            resume: new Dictionary<string, string> { ["zone"] = "Asia/Tokyo" });
        Assert.True(live.Passed);
        Assert.Equal("Hi Asia/Tokyo", live.Values["message"]);

        Assert.True(changes.Revert(promoted.ChangeSet!.ChangeSetId, "rollback", _clock.UtcNow).Ok);
        Assert.False(store.IsPromoted("zone_then_say"));
    }

    [Fact]
    public async Task Build_rejected_without_runner()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s5-nobuild");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new WorldClockToolMind(), diagnostics);
        Assert.False(runtime.ToolBuildAvailable);
        var started = runtime.StartDirectCase(WorldClockToolMind.TokyoAsk);
        var stepped = await runtime.StepCaseAsync(started.Id);
        Assert.Null(runtime.GetPendingApproval(started.Id));
        Assert.Contains(runtime.Cases.LoadEvents(started.Id), e => e.Type == CaseEventTypes.MoveRejected);
    }

    private static RuntimeDiagnostics Diag(string runId)
    {
        var path = Path.Combine(Path.GetTempPath(), "relay-s5-" + runId + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        return new RuntimeDiagnostics(path, runId);
    }

    public void Dispose() => _tmp.Dispose();

    /// <summary>Proposes tool.revert for a change set, then stops after completion.</summary>
    private sealed class ScriptedRevertMind(string changeSetId) : ICaseMind
    {
        public string Name => "scripted-revert";
        public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
        {
            var awaiting = request.PendingOperations.FirstOrDefault(o =>
                o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);
            if (awaiting is not null)
                return Task.FromResult(new CaseMindStep(null, new CaseMove { Type = CaseMove.Wait, Text = "await" }, "wait"));

            var done = request.RecentEvents.Any(e =>
                e.Type == CaseEventTypes.OperationExecuted
                && e.Payload.TryGetProperty("capability", out var c)
                && c.GetString() == ToolCapabilities.RevertTool);
            if (done)
                return Task.FromResult(new CaseMindStep(null, new CaseMove { Type = CaseMove.Stop, Text = "reverted", Done = true }, "done"));

            return Task.FromResult(new CaseMindStep(null, new CaseMove
            {
                Type = CaseMove.Propose,
                Name = ToolCapabilities.RevertTool,
                Text = "Revert the world_clock promotion",
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["capability"] = JsonSerializer.SerializeToElement(ToolCapabilities.RevertTool),
                    ["changeSetId"] = JsonSerializer.SerializeToElement(changeSetId),
                    ["reason"] = JsonSerializer.SerializeToElement("rollback after use"),
                    ["idempotencyKey"] = JsonSerializer.SerializeToElement("revert-" + changeSetId),
                },
            }, "propose revert"));
        }
    }
}
