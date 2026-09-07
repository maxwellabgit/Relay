using Relay.Core.Attention;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Orchestration;
using Relay.Core.Preferences;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The arbiter decides how much attention a finished task gets: origin sets the floor, evidence the
/// ceiling, preferences the budgets and cool-downs, merge keys stop one finding from carding twice.
/// The first half tests the arbiter alone; the second half drives it through listening scenarios.
/// </summary>
public class AttentionTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static CompiledPreferences Prefs(int cooldownSeconds = 300, int maxAlerts = 3, int maxResults = 6)
        => PreferenceCompiler.Compile(new UserPreferences { Display = { CooldownSeconds = cooldownSeconds, MaxAlertsPer10Minutes = maxAlerts, MaxResultsPer5Minutes = maxResults } });

    private static AttentionInput Input(string taskId, TaskOrigin origin = TaskOrigin.Observed, TaskKind kind = TaskKind.Check, string? mergeKey = null,
        double confidence = 0.9, bool hasAnswer = true, bool? consistent = false, int sources = 2, int pending = 0, int executed = 0, bool failed = false,
        string? watched = null, DateTimeOffset? at = null, int rejected = 0, Presentation? suggested = null)
        => new(taskId, origin, kind, "Title " + taskId, "Detail " + taskId, mergeKey, suggested, confidence, hasAnswer, consistent, sources, pending, executed, failed, watched, at ?? T0, rejected);

    // ----------------------------------------------------------------------------------------
    // The rank table
    // ----------------------------------------------------------------------------------------

    public static IEnumerable<object[]> RankCases()
    {
        yield return [Input("direct-answer", TaskOrigin.Direct, TaskKind.Answer, consistent: null, sources: 0), Presentation.Result, "always answered"];
        yield return [Input("direct-consistent", TaskOrigin.Direct, TaskKind.Check, consistent: true), Presentation.Result, "always answered"];
        yield return [Input("direct-research", TaskOrigin.Direct, TaskKind.Research, consistent: null), Presentation.Findings, "findings"];
        yield return [Input("direct-failed", TaskOrigin.Direct, TaskKind.Answer, failed: true), Presentation.Result, "failed"];
        yield return [Input("observed-failed", TaskOrigin.Observed, TaskKind.Check, failed: true), Presentation.Ambient, "failed"];
        yield return [Input("pending", TaskOrigin.Observed, TaskKind.Organize, pending: 2), Presentation.Proposal, "await approval"];
        yield return [Input("consistent", consistent: true), Presentation.None, "agrees"];
        yield return [Input("conflict", consistent: false, sources: 2, confidence: 0.8), Presentation.Alert, "conflict"];
        yield return [Input("thin-sources", consistent: false, sources: 1, confidence: 0.9), Presentation.Result, "thin"];
        yield return [Input("thin-confidence", consistent: false, sources: 2, confidence: 0.5), Presentation.Result, "thin"];
        yield return [Input("undecided", consistent: null), Presentation.None, "could not be decided"];
        yield return [Input("remember", kind: TaskKind.Remember, consistent: null, executed: 1), Presentation.Ambient, "note filed"];
        yield return [Input("remember-inbox", kind: TaskKind.Remember, consistent: null), Presentation.Ambient, "inbox"];
        yield return [Input("resolve", kind: TaskKind.Resolve, consistent: null), Presentation.Result, "definition found"];
        yield return [Input("resolve-none", kind: TaskKind.Resolve, consistent: null, hasAnswer: false), Presentation.None, "no definition"];
        yield return [Input("resolve-watched", kind: TaskKind.Resolve, consistent: null, watched: "CAD"), Presentation.Result, "watched term"];
        yield return [Input("research", kind: TaskKind.Research, consistent: null), Presentation.Findings, "findings ready"];
        yield return [Input("organize-granted", kind: TaskKind.Organize, consistent: null, executed: 1), Presentation.Ambient, "operation(s) ran"];
        yield return [Input("organize-nothing", kind: TaskKind.Organize, consistent: null), Presentation.None, "nothing to propose"];
        yield return [Input("answer-overheard", kind: TaskKind.Answer, consistent: null), Presentation.Result, "overheard"];
        yield return [Input("executed", consistent: false, executed: 1), Presentation.Ambient, "operation(s) ran"];
        yield return [Input("rejected", consistent: false, rejected: 1), Presentation.None, "declined"];
    }

    [Theory]
    [MemberData(nameof(RankCases))]
    public void TheRankTableFollowsOriginEvidenceAndOutcome(AttentionInput input, Presentation expected, string reasonFragment)
    {
        var arbiter = new AttentionArbiter(() => Prefs());
        var (level, reason) = arbiter.RankOnly(input);
        Assert.Equal(expected, level);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheModelsSuggestionNeverOverridesTheEvidence()
    {
        var arbiter = new AttentionArbiter(() => Prefs());
        // The judge suggested an alert; one source is not enough evidence for one.
        Assert.Equal(Presentation.Result, arbiter.RankOnly(Input("t1", consistent: false, sources: 1, suggested: Presentation.Alert)).Level);
        // The judge suggested nothing; a consistent statement is nothing regardless.
        Assert.Equal(Presentation.None, arbiter.RankOnly(Input("t2", consistent: true, suggested: Presentation.Alert)).Level);
        // A direct ask is answered even if the model suggested silence.
        Assert.Equal(Presentation.Result, arbiter.RankOnly(Input("t3", TaskOrigin.Direct, consistent: true, suggested: Presentation.None)).Level);
    }

    // ----------------------------------------------------------------------------------------
    // Merge, cool-down, dismissal
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void TheSameKeyInsideTheCooldownMergesIntoOneCardAndKeepsTheHigherLevel()
    {
        var arbiter = new AttentionArbiter(() => Prefs(cooldownSeconds: 300));
        var first = arbiter.Decide(Input("t1", mergeKey: "check:atlas:date", sources: 1));               // thin evidence: result
        var second = arbiter.Decide(Input("t2", mergeKey: "check:atlas:date", sources: 2, at: T0.AddSeconds(60)));  // full evidence: alert

        Assert.False(first.Merged);
        Assert.True(second.Merged);
        var card = Assert.Single(arbiter.Items);
        Assert.Equal(first.Item!.ItemId, card.ItemId);                                                    // refreshed in place, same card
        Assert.Equal(Presentation.Alert, card.Level);                                                     // escalated to the higher level
        Assert.Equal(2, card.Occurrences);
        Assert.Equal(["t1", "t2"], card.TaskIds);
        Assert.Equal("t2", card.TaskId);                                                                  // points at the latest task
        Assert.Equal("Title t2", card.Title);
        Assert.Equal(T0, card.FirstAt);
        Assert.Equal(T0.AddSeconds(60), card.LastAt);
        Assert.Contains("merged", second.Reason);

        // A later, weaker finding on the same key does not lower the card.
        var third = arbiter.Decide(Input("t3", mergeKey: "check:atlas:date", sources: 1, at: T0.AddSeconds(120)));
        Assert.True(third.Merged);
        Assert.Equal(Presentation.Alert, Assert.Single(arbiter.Items).Level);
        Assert.Equal(3, Assert.Single(arbiter.Items).Occurrences);
    }

    [Fact]
    public void TheSameKeyAfterTheCooldownIsAFreshCardThatReplacesTheOld()
    {
        var arbiter = new AttentionArbiter(() => Prefs(cooldownSeconds: 60));
        var first = arbiter.Decide(Input("t1", mergeKey: "k"));
        var second = arbiter.Decide(Input("t2", mergeKey: "k", at: T0.AddSeconds(61)));

        Assert.False(second.Merged);
        var card = Assert.Single(arbiter.Items);                                                          // still one card for the key
        Assert.NotEqual(first.Item!.ItemId, card.ItemId);
        Assert.Equal(1, card.Occurrences);
        Assert.Equal(["t2"], card.TaskIds);
    }

    [Fact]
    public void ADismissedFindingStaysAwayInsideTheCooldownUnlessItEscalates()
    {
        var arbiter = new AttentionArbiter(() => Prefs(cooldownSeconds: 300));
        var shown = arbiter.Decide(Input("t1", mergeKey: "k", sources: 1));                               // result
        Assert.True(arbiter.Dismiss(shown.Item!.ItemId, T0.AddSeconds(10)));
        Assert.Empty(arbiter.Items);
        Assert.False(arbiter.Dismiss(shown.Item.ItemId, T0.AddSeconds(11)));                              // gone already

        var again = arbiter.Decide(Input("t2", mergeKey: "k", sources: 1, at: T0.AddSeconds(30)));         // same level, inside the cool-down
        Assert.Equal(Presentation.None, again.Level);
        Assert.Null(again.Item);
        Assert.Contains("dismissed", again.Reason);
        Assert.Empty(arbiter.Items);

        var escalated = arbiter.Decide(Input("t3", mergeKey: "k", sources: 2, at: T0.AddSeconds(40)));     // an alert now: worth showing again
        Assert.Equal(Presentation.Alert, escalated.Level);
        Assert.NotNull(escalated.Item);
        Assert.Single(arbiter.Items);
    }

    [Fact]
    public void ADismissedFindingComesBackAfterTheCooldown()
    {
        var arbiter = new AttentionArbiter(() => Prefs(cooldownSeconds: 60));
        var shown = arbiter.Decide(Input("t1", mergeKey: "k", sources: 1));
        arbiter.Dismiss(shown.Item!.ItemId, T0.AddSeconds(5));
        var later = arbiter.Decide(Input("t2", mergeKey: "k", sources: 1, at: T0.AddSeconds(66)));
        Assert.Equal(Presentation.Result, later.Level);
        Assert.NotNull(later.Item);
    }

    [Fact]
    public void UnrelatedFindingsNeverMerge()
    {
        var arbiter = new AttentionArbiter(() => Prefs());
        arbiter.Decide(Input("t1", mergeKey: "a"));
        arbiter.Decide(Input("t2", mergeKey: "b", at: T0.AddSeconds(1)));
        arbiter.Decide(Input("t3", at: T0.AddSeconds(2)));                                                 // no key: keyed by its own task
        arbiter.Decide(Input("t4", at: T0.AddSeconds(3)));
        Assert.Equal(4, arbiter.Items.Count);
        Assert.All(arbiter.Items, i => Assert.Equal(1, i.Occurrences));
    }

    // ----------------------------------------------------------------------------------------
    // Budgets
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void BeyondTheAlertBudgetAnAlertIsShownAsAResultNeverDropped()
    {
        var arbiter = new AttentionArbiter(() => Prefs(maxAlerts: 2));
        var a = arbiter.Decide(Input("t1", mergeKey: "a"));
        var b = arbiter.Decide(Input("t2", mergeKey: "b", at: T0.AddMinutes(1)));
        var c = arbiter.Decide(Input("t3", mergeKey: "c", at: T0.AddMinutes(2)));
        Assert.Equal(Presentation.Alert, a.Level);
        Assert.Equal(Presentation.Alert, b.Level);
        Assert.Equal(Presentation.Result, c.Level);
        Assert.NotNull(c.Item);
        Assert.Contains("alert budget (2/10 min) reached", c.Reason);
        Assert.Equal(3, arbiter.Items.Count);

        // Eleven minutes after the first alert, the budget has room again.
        var d = arbiter.Decide(Input("t4", mergeKey: "d", at: T0.AddMinutes(11)));
        Assert.Equal(Presentation.Alert, d.Level);
    }

    [Fact]
    public void BeyondTheResultBudgetAResultBecomesAmbient()
    {
        var arbiter = new AttentionArbiter(() => Prefs(maxResults: 2));
        for (var i = 0; i < 2; i++) Assert.Equal(Presentation.Result, arbiter.Decide(Input("r" + i, kind: TaskKind.Resolve, mergeKey: "r" + i, consistent: null, at: T0.AddSeconds(i))).Level);
        var over = arbiter.Decide(Input("r9", kind: TaskKind.Resolve, mergeKey: "r9", consistent: null, at: T0.AddSeconds(10)));
        Assert.Equal(Presentation.Ambient, over.Level);
        Assert.Contains("result budget (2/5 min) reached", over.Reason);
        Assert.NotNull(over.Item);                                                                         // still on the list, quietly
    }

    [Fact]
    public void AlertsDowngradedByBudgetCountAgainstTheResultBudgetNotTheAlertBudget()
    {
        var arbiter = new AttentionArbiter(() => Prefs(maxAlerts: 1, maxResults: 1));
        Assert.Equal(Presentation.Alert, arbiter.Decide(Input("t1", mergeKey: "a")).Level);
        Assert.Equal(Presentation.Result, arbiter.Decide(Input("t2", mergeKey: "b", at: T0.AddSeconds(1))).Level);   // alert budget spent
        Assert.Equal(Presentation.Ambient, arbiter.Decide(Input("t3", mergeKey: "c", at: T0.AddSeconds(2))).Level);  // result budget spent too
    }

    [Fact]
    public void MergedRefreshesDoNotSpendTheBudget()
    {
        var arbiter = new AttentionArbiter(() => Prefs(maxAlerts: 1));
        arbiter.Decide(Input("t1", mergeKey: "a"));
        arbiter.Decide(Input("t2", mergeKey: "a", at: T0.AddSeconds(30)));                                 // merged into the first
        arbiter.Decide(Input("t3", mergeKey: "a", at: T0.AddSeconds(60)));
        Assert.Equal(3, Assert.Single(arbiter.Items).Occurrences);
        // A different topic finds the budget spent by exactly one alert, and is downgraded once.
        Assert.Equal(Presentation.Result, arbiter.Decide(Input("t4", mergeKey: "b", at: T0.AddSeconds(90))).Level);
    }

    // ----------------------------------------------------------------------------------------
    // Pinned definitions and ordering
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void PinnedDefinitionsBypassTheResultBudgetRefreshInPlaceAndSortFirst()
    {
        var arbiter = new AttentionArbiter(() => Prefs(maxResults: 1, cooldownSeconds: 1));
        arbiter.Decide(Input("t1", kind: TaskKind.Resolve, mergeKey: "def:x", consistent: null));         // spends the result budget
        var pinned = arbiter.Decide(Input("t2", kind: TaskKind.Resolve, mergeKey: "define:cad", consistent: null, watched: "CAD", at: T0.AddSeconds(1)));
        Assert.Equal(Presentation.Result, pinned.Level);
        Assert.True(pinned.Item!.Pinned);

        // Long after the cool-down, the same term is refreshed in place rather than re-carded.
        var refreshed = arbiter.Decide(Input("t3", kind: TaskKind.Resolve, mergeKey: "define:cad", consistent: null, watched: "CAD", at: T0.AddMinutes(30)));
        Assert.True(refreshed.Merged);
        Assert.Equal(pinned.Item.ItemId, refreshed.Item!.ItemId);
        Assert.Equal("Title t3", refreshed.Item.Title);

        var alert = arbiter.Decide(Input("t4", mergeKey: "conflict", at: T0.AddMinutes(31)));
        Assert.Equal(Presentation.Alert, alert.Level);

        var order = arbiter.Items.Select(i => i.Key).ToList();
        Assert.Equal("define:cad", order[0]);                                                              // pinned first
        Assert.Equal("conflict", order[1]);                                                                // then by level
        Assert.Equal("def:x", order[2]);
    }

    [Fact]
    public void ResolvingAProposalCardRemovesItOrDowngradesIt()
    {
        var arbiter = new AttentionArbiter(() => Prefs());
        arbiter.Decide(Input("t1", pending: 1));
        arbiter.Decide(Input("t2", pending: 1, at: T0.AddSeconds(1)));
        Assert.Equal(2, arbiter.Items.Count(i => i.Level == Presentation.Proposal));

        arbiter.Resolve("t1", null, "", "", T0.AddSeconds(2));
        Assert.DoesNotContain(arbiter.Items, i => i.TaskId == "t1");

        arbiter.Resolve("t2", Presentation.Ambient, "Moved", "3 notes moved", T0.AddSeconds(3));
        var left = Assert.Single(arbiter.Items);
        Assert.Equal(Presentation.Ambient, left.Level);
        Assert.Equal("Moved", left.Title);
        Assert.Equal("proposal resolved", left.Reason);

        arbiter.Resolve("nobody", null, "", "", T0);                                                       // unknown task: nothing happens
        Assert.Single(arbiter.Items);
    }

    // ----------------------------------------------------------------------------------------
    // Through the coordinator: listening scenarios
    // ----------------------------------------------------------------------------------------

    private const string Chatter = "Anyway the coffee machine is broken again.";

    /// <summary>A planner that reports a conflict with two sources for every check task, and proposes nothing.</summary>
    private static CannedOrchestrator ConflictPlanner() => new CannedOrchestrator().Otherwise((request, _) =>
        request.Kind == TaskKind.Check
            ? new TurnPlan(true, "The stated date disagrees with the stored decision", ["Searched", "Compared"],
                "Stored: October 14. Heard: the 21st. These conflict.",
                [new Citation(SearchIndex.NoteKind, "01NOTE00000ATLAS", null, "atlas", "Atlas beta ships on October 14.", null), new Citation(SearchIndex.ExcerptKind, request.ExcerptId ?? "01EXCERPT000000X", null, null, "the 21st", null)],
                [], "canned", Consistent: false)
            : TurnPlan.NotUnderstood("canned", "only check tasks are scripted"));

    [Fact]
    public void RepeatedConflictsOnOneTopicShareOneCardAndADismissedCardStaysAway()
    {
        var judge = new ScriptedJudge()
            .When("21st", TaskKind.Check, "Check the stated date.", topic: "atlas beta date", mergeKey: "check:atlas:date")
            .When("22nd", TaskKind.Check, "Check the stated date.", topic: "atlas beta date", mergeKey: "check:atlas:date")
            .When("23rd", TaskKind.Check, "Check the stated date.", topic: "atlas beta date", mergeKey: "check:atlas:date");
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: ConflictPlanner()).WithWorkspace()
            .WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Alert, "Conflict");
        var card = Assert.Single(s.Snap.Attention);
        Assert.Equal(1, card.Occurrences);

        s.Listen(Chatter)
            .Listen("Sales now says the 22nd.")
            .ExpectEvent(EventTypes.TaskMerged, 1);
        card = Assert.Single(s.Snap.Attention);                                                            // the same finding again: one card, refreshed
        Assert.Equal(2, card.Occurrences);
        Assert.Equal(Presentation.Alert, card.Level);
        Assert.Equal(2, card.TaskIds.Count);
        Assert.Equal(2, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check));                                 // both tasks exist and are diagnosed
        var merged = s.H.Last(EventTypes.TaskMerged)!;
        // A merge key is the judge's words about the room, so the ledger holds its fingerprint — the same one on every task that shares it.
        Assert.StartsWith("withheld: ", merged.DataString("key"));
        Assert.All(s.H.Records().Where(r => r.Type == EventTypes.TaskCreated && r.DataString("kind") == "check"), r => Assert.Equal(merged.DataString("key"), r.DataString("mergeKey")));
        Assert.Equal(card.TaskIds[0], merged.DataString("into"));

        s.DismissAttention("Conflict")
            .ExpectEvent(EventTypes.AttentionDismissed)
            .ExpectNoAttention()
            .Listen("Legal insists on the 23rd.")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectNoAttention()                                                                           // dismissed at this level, inside the cool-down
            .ExpectEvent(EventTypes.AttentionSuppressed);
        var suppressed = s.H.Last(EventTypes.AttentionSuppressed)!;
        Assert.Contains("dismissed", suppressed.DataString("reason"));
        Assert.Equal(3, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check));                                 // the task still ran and was recorded
        var third = s.Snap.Tasks.Where(t => t.Kind == TaskKind.Check).OrderBy(t => t.StartedAt).Last();
        Assert.Equal(Presentation.None, third.Presentation);
        Assert.Contains("dismissed", third.PresentationReason);
    }

    [Fact]
    public void TheAlertBudgetFromPreferencesTurnsTheThirdAlertIntoAResult()
    {
        var judge = new ScriptedJudge()
            .When("Atlas", TaskKind.Check, "Check.", topic: "atlas", mergeKey: "check:atlas")
            .When("Backyard", TaskKind.Check, "Check.", topic: "backyard", mergeKey: "check:backyard")
            .When("Lightshift", TaskKind.Check, "Check.", topic: "lightshift", mergeKey: "check:lightshift");
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: ConflictPlanner()).WithWorkspace()
            .Do("Two alerts per ten minutes", c => Assert.True(c.UpdatePreference("display.maxAlertsPer10Minutes", "2")))
            .ExpectPreference("display.maxAlertsPer10Minutes", "2")
            .WithListening().StartListening()
            .Listen("Atlas slipped a week.")
            .Listen("Backyard is over budget.")
            .Listen("Lightshift lost its sponsor.");

        Assert.Equal(2, s.Snap.Attention.Count(a => a.Level == Presentation.Alert));
        var downgraded = Assert.Single(s.Snap.Attention, a => a.Level == Presentation.Result);
        Assert.Contains("Lightshift", downgraded.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alert budget (2/10 min) reached", downgraded.Reason);
        var shown = s.H.Records().Where(r => r.Type == EventTypes.AttentionShown).ToList();
        Assert.Equal(["alert", "alert", "result"], shown.Select(r => r.DataString("level")).ToList());
        Assert.Equal(3, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check && t.Status == TaskStatus.Completed));
    }

    [Fact]
    public void AConsistentObservationShowsNothingButIsFullyRecorded()
    {
        var judge = new ScriptedJudge().When("October 14", TaskKind.Check, "Check the stated date.", topic: "atlas beta date", mergeKey: "check:atlas:date");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Agrees with the stored decision", ["Searched"], "Consistent with the decision of October 14.",
            [new Citation(SearchIndex.NoteKind, "01NOTE00000ATLAS", null, "atlas", "Atlas beta ships on October 14.", null)], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace()
            .WithListening().StartListening()
            .Listen("So the beta still ships October 14, right?")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectNoAttention()
            .ExpectEvent(EventTypes.AttentionSuppressed)
            .ExpectNoEvent(EventTypes.AttentionShown);
        var task = s.FindTask(TaskKind.Check)!;
        Assert.True(task.Consistent);
        Assert.Equal(Presentation.None, task.Presentation);
        Assert.Contains("agrees", task.PresentationReason);
        Assert.True(File.Exists(Path.Combine(s.H.Root.TasksDirectory, task.TaskId + ".json")));            // diagnostics kept for the evaluation loop
    }

    [Fact]
    public void AThinConflictIsAResultNotAnAlert()
    {
        var judge = new ScriptedJudge().When("21st", TaskKind.Check, "Check the stated date.", confidence: 0.9, topic: "atlas beta date", mergeKey: "check:atlas:date");
        var planner = new CannedOrchestrator().Otherwise((request, _) => new TurnPlan(true, "Possibly disagrees", ["Searched"], "One note says October 14; the room said the 21st.",
            [new Citation(SearchIndex.NoteKind, "01NOTE00000ATLAS", null, "atlas", "Atlas beta ships on October 14.", null)], [], "canned", Consistent: false));   // one source only
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace()
            .WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectAttention(Presentation.Result, "Conflict")
            .ExpectNoAttention(Presentation.Alert);
        Assert.Contains("evidence is thin", s.FindTask(TaskKind.Check)!.PresentationReason);
    }

    [Fact]
    public void ALowConfidenceJudgeFindingCannotAlert()
    {
        var judge = new ScriptedJudge().When("21st", TaskKind.Check, "Check the stated date.", confidence: 0.58, topic: "atlas beta date", mergeKey: "check:atlas:date");
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: ConflictPlanner()).WithWorkspace()
            .WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Result)
            .ExpectNoAttention(Presentation.Alert);
        Assert.Contains("confidence 0.58", s.FindTask(TaskKind.Check)!.PresentationReason);
    }

    [Fact]
    public void TheUsersReactionToACardIsRecordedOnTheTask()
    {
        var judge = new ScriptedJudge().When("21st", TaskKind.Check, "Check the stated date.", topic: "atlas beta date", mergeKey: "check:atlas:date");
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: ConflictPlanner()).WithWorkspace()
            .WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectAttention(Presentation.Alert)
            .Respond("not needed, marketing does not set dates")
            .ExpectEvent(EventTypes.TaskUserResponse);
        var task = s.FindTask(TaskKind.Check)!;
        Assert.Equal("not needed, marketing does not set dates", task.UserResponse);
        var diagnostics = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(s.H.Root.TasksDirectory, task.TaskId + ".json"))).RootElement;
        Assert.Equal("not needed, marketing does not set dates", diagnostics.GetProperty("userResponse").GetString());
        Assert.Equal("alert", diagnostics.GetProperty("presentation").GetString());
    }

    public void Dispose() => _tmp.Dispose();
}
