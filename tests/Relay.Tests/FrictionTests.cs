using Relay.Core.Decisions;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Core.Usage;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// Alpha Step 6: on idle (or an explicit review call), repeated usage friction becomes one Improve
/// proposal that still needs approval; a second review in the same day is a no-op.
/// </summary>
public class FrictionTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static UsageLine Line(DateTimeOffset at, string taskId, IReadOnlyList<string> needs, string? route = null, string outcome = "answered", long wallMs = 1_000, int steps = 2)
        => new(at, taskId, InputObserved.Ask, "mind:test", route, 0.4, needs, 0.3, steps, 0, 0, 100, 40, wallMs, outcome,
            new Dictionary<string, string>(StringComparer.Ordinal), null, null);

    private void Seed(params UsageLine[] lines)
    {
        var usage = new UsageRecorder(_tmp.Root);
        foreach (var line in lines) usage.Record(line);
    }

    [Fact]
    public void SuggestFindsRepeatedNewToolNeedAndCarriesExamples()
    {
        var at = Harness.T0;
        var lines = Enumerable.Range(1, 3).Select(i => Line(at, "t" + i, [MindRead.NeedNewTool])).ToList();
        var suggestion = FrictionReview.Suggest(lines);
        Assert.NotNull(suggestion);
        Assert.Equal("need:new_tool", suggestion.Pattern);
        Assert.Equal(Actions.UpdatePreference, suggestion.Action);
        Assert.Equal("response.promptLine", suggestion.Target["key"]);
        Assert.Equal(3, suggestion.ExampleTaskIds.Count);
        foreach (var key in PolicyEngine.ContractKeys) Assert.False(string.IsNullOrWhiteSpace(suggestion.Target.GetValueOrDefault(key)));
        Assert.Contains("t1", suggestion.Reason);
        Assert.Null(FrictionReview.Suggest(lines.Take(2).ToList()));
    }

    [Fact]
    public void SuggestPrefersSearchNeedOverLongPathWhenBothQualify()
    {
        var at = Harness.T0;
        var lines = new List<UsageLine>
        {
            Line(at, "s1", [MindRead.NeedExternalReasoning], wallMs: 60_000, steps: 10),
            Line(at, "s2", [MindRead.NeedWorldKnowledge], wallMs: 60_000, steps: 10),
            Line(at, "s3", [MindRead.NeedExternalReasoning], wallMs: 60_000, steps: 10),
        };
        var suggestion = FrictionReview.Suggest(lines);
        Assert.NotNull(suggestion);
        Assert.Equal("need:search", suggestion.Pattern);
        Assert.Equal("sources.allowOnlineSearch", suggestion.Target["key"]);
        Assert.Equal("true", suggestion.Target["value"]);
    }

    [Fact]
    public void ReviewProposesOnceThenNoOpsAndApprovalAppliesThePreference()
    {
        var at = Harness.T0;
        Seed(
            Line(at, "t1", [MindRead.NeedNewTool]),
            Line(at, "t2", [MindRead.NeedNewTool]),
            Line(at, "t3", [MindRead.NeedNewTool]));

        using var h = new Harness(_tmp.Root).Start();
        Assert.Equal(RelayState.Ready, h.Snap.State);

        Assert.True(h.Coordinator.TryReviewFriction("idle"));
        var reviewed = h.Last(EventTypes.FrictionReviewed);
        Assert.NotNull(reviewed);
        Assert.True(reviewed.DataBool("proposed"));
        Assert.Equal("need:new_tool", reviewed.DataString("pattern"));
        Assert.Equal(Actions.UpdatePreference, reviewed.DataString("action"));

        var improve = Assert.Single(h.Snap.Tasks, t => t.Kind == TaskKind.Improve && t.Lane == "friction");
        Assert.Equal(TaskStatus.AwaitingApproval, improve.Status);
        Assert.Equal(TaskOrigin.Direct, improve.Origin);
        var card = Assert.Single(improve.Proposals);
        Assert.Equal(Actions.UpdatePreference, card.Action);
        Assert.Equal("pending", card.Status);
        Assert.Contains("Benefit:", card.Detail);
        Assert.Contains("t1", card.Reason);

        // Second call in the same session / day is a no-op.
        Assert.False(h.Coordinator.TryReviewFriction("idle"));
        Assert.Equal(1, h.Records().Count(r => r.Type == EventTypes.FrictionReviewed));
        Assert.Single(h.Snap.Tasks, t => t.Lane == "friction");

        h.Coordinator.Approve(card.ProposalId);
        Assert.Equal("executed", Assert.Single(h.Snap.Tasks, t => t.TaskId == improve.TaskId).Outcome);
        Assert.Contains(h.Snap.ChangeSets, c => !c.Reverted);
        var prefs = h.Preferences.Load();
        Assert.Contains(prefs.Response.PromptLines, line => line.Contains("capability gap", StringComparison.OrdinalIgnoreCase)
            || line.Contains("personal tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdleDebounceAfterTasksSettleRunsTheReview()
    {
        var at = Harness.T0;
        Seed(
            Line(at, "d1", [], route: Decider.OfferDelegate, outcome: "answered"),
            Line(at, "d2", [], route: Decider.OfferDelegate, outcome: "answered"),
            Line(at, "d3", [], route: Decider.OfferDelegate, outcome: "answered"));

        using var h = new Harness(_tmp.Root, mind: new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Say("ok."), "done"))).Start();
        // Finish a trivial ask so ScheduleFrictionReview arms; then advance the debounce.
        Assert.True(h.Coordinator.SubmitDirect("ping"));
        h.Scheduler.Advance(TimeSpan.FromSeconds(3));

        Assert.NotNull(h.Last(EventTypes.FrictionReviewed));
        Assert.Contains(h.Snap.Tasks, t => t.Lane == "friction" && t.Kind == TaskKind.Improve);
    }

    [Fact]
    public void ShutdownDefersAFindingWithoutCreatingATask()
    {
        var at = Harness.T0;
        Seed(
            Line(at, "x1", [], outcome: "denied"),
            Line(at, "x2", [], outcome: "denied"),
            Line(at, "x3", [], outcome: "denied"));

        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.Shutdown("test");
        var reviewed = h.Last(EventTypes.FrictionReviewed);
        Assert.NotNull(reviewed);
        Assert.True(reviewed.DataBool("proposed"));
        Assert.True(reviewed.DataBool("deferred"));
        Assert.DoesNotContain(h.Snap.Tasks, t => t.Lane == "friction");
    }
}
