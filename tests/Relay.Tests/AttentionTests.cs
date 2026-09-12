using Relay.Core.Attention;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Preferences;
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
        // The model suggested an alert; one source is not enough evidence for one.
        Assert.Equal(Presentation.Result, arbiter.RankOnly(Input("t1", consistent: false, sources: 1, suggested: Presentation.Alert)).Level);
        // The model suggested nothing; a consistent statement is nothing regardless.
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

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Chatter = "Anyway the coffee machine is broken again.";

    /// <summary>
    /// The mind runs everything. Listening stays off while the world is built, so the note chord dictates the
    /// record a check is later measured against; <see cref="Scenario.WithListening"/> turns it on.
    /// </summary>
    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    /// <summary>A raise over one line, under the topic and merge key these scenarios share.</summary>
    private static RaiseMove Check(string objective, WindowLine line)
        => new("check", objective, [line.Label], "a dated claim about a known project", Topic: "atlas beta date", MergeKey: "check:atlas:date");

    /// <summary>The one note filed under a project: the record a raised check opens first.</summary>
    private static (string ProjectId, string NoteId) StoredIn(Scenario s, string slug)
    {
        var project = s.H.Registry.FindActive(slug) ?? throw s.Fail($"No active project '{slug}' to check against");
        return (project.Id, Assert.Single(ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note)).Id);
    }

    /// <summary>The excerpt the raise anchored to: the words being checked.</summary>
    private static string Heard(MindRequest request) => request.Transcript.OfType<InputObserved>().First().ExcerptId!;

    /// <summary>What the raise asked the task to do; it names the topic the check is about.</summary>
    private static string Objective(MindRequest request) => request.Transcript.OfType<InputObserved>().First().Text;

    /// <summary>
    /// What a raised check does: open the stored decision it is about, open the words it was raised from, and
    /// end with the verdict. Evidence is what was opened by id — a search returns candidates and grounds
    /// nothing — so a check that alerts has to hold both sides in its hands.
    /// </summary>
    private static ScriptedMind Conflicting(Func<MindRequest, (string ProjectId, string NoteId)> record) => new ScriptedMind().Always(request =>
    {
        var opened = request.Transcript.OfType<ToolObserved>().Count();
        if (opened == 0)
        {
            var (projectId, noteId) = record(request);
            return MindStep.Of(ScriptedMind.Tool("read_note", ("projectId", projectId), ("noteId", noteId)), "Reading the stored decision.");
        }
        if (opened == 1) return MindStep.Of(ScriptedMind.Tool("read_excerpt", ("excerptId", Heard(request))), "Reading back what was just said.");
        return MindStep.Of(ScriptedMind.Say("The stored decision and what was just said name different dates."),
            "What was said disagrees with the stored decision.", ScriptedMind.Verdict(consistent: false));
    });

    private static ScriptedMind Conflicting(string projectId, string noteId) => Conflicting(_ => (projectId, noteId));

    [Fact]
    public void RepeatedConflictsOnOneTopicShareOneCardAndADismissedCardStaysAway()
    {
        // One merge key over three mentions; each objective differs, because the loop refuses a repeat of one.
        var mind = new ListeningMind();
        foreach (var date in new[] { "21st", "22nd", "23rd" })
            mind.When(date, line => Check($"Check the date stated in line {line.Label}.", line));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note(Decision);
        var (projectId, noteId) = StoredIn(s, "atlas");
        mind.Works(Conflicting(projectId, noteId));

        s.WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Alert, "Conflict");
        var card = Assert.Single(s.Snap.Attention);
        Assert.Equal(1, card.Occurrences);
        Assert.Equal(2, s.FindTask(TaskKind.Check)!.Citations.Count);                                       // the stored decision and the words it was checked against

        s.Listen(Chatter)
            .Listen("Sales now says the 22nd.")
            .ExpectEvent(EventTypes.TaskMerged, 1);
        card = Assert.Single(s.Snap.Attention);                                                            // the same finding again: one card, refreshed
        Assert.Equal(2, card.Occurrences);
        Assert.Equal(Presentation.Alert, card.Level);
        Assert.Equal(2, card.TaskIds.Count);
        Assert.Equal(2, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check));                                 // both tasks exist and are diagnosed
        var merged = s.H.Last(EventTypes.TaskMerged)!;
        // A merge key is the mind's words about the room, so the ledger holds its fingerprint — the same one on every task that shares it.
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
        var projects = new[] { "Atlas", "Backyard", "Lightshift" };
        var mind = new ListeningMind();
        foreach (var project in projects)
            mind.When(project, "check", $"Check what was said about {project}.", topic: project.ToLowerInvariant(), mergeKey: "check:" + project.ToLowerInvariant());
        // Three topics, each with a decision of its own on record: a check can only alert about something it can open.
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas").Note(Decision)
            .Project("Backyard").Note("We decided the Backyard budget is twelve thousand.")
            .Project("Lightshift").Note("We decided the Lightshift pilot runs through March.")
            .Do("Two alerts per ten minutes", c => Assert.True(c.UpdatePreference("display.maxAlertsPer10Minutes", "2")))
            .ExpectPreference("display.maxAlertsPer10Minutes", "2");
        var records = projects.Select(name => (Name: name, Stored: StoredIn(s, name.ToLowerInvariant()))).ToList();
        // Each check opens the decision of the project its objective names; every topic is its own card and its own budget line.
        mind.Works(Conflicting(request => records.First(r => Objective(request).Contains(r.Name, StringComparison.OrdinalIgnoreCase)).Stored));

        s.WithListening().StartListening()
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
        var mind = new ListeningMind().When("October 14", line => Check("Check the stated date.", line));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note(Decision);
        var (projectId, noteId) = StoredIn(s, "atlas");
        mind.Works(new ScriptedMind().Always(request => request.Transcript.OfType<ToolObserved>().Any()
            ? MindStep.Of(ScriptedMind.Say("Stored and heard agree: October 14."), "Agrees with the stored decision.", ScriptedMind.Verdict(consistent: true))
            : MindStep.Of(ScriptedMind.Tool("read_note", ("projectId", projectId), ("noteId", noteId)), "Reading the stored decision.")));

        s.WithListening().StartListening()
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
        var mind = new ListeningMind { Significance = 0.9 }.When("21st", line => Check("Check the stated date.", line));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note(Decision);
        var (projectId, noteId) = StoredIn(s, "atlas");
        // The stored decision is opened and the words heard are not: a finding on one source, however sure the mind is of it.
        mind.Works(new ScriptedMind().Always(request => request.Transcript.OfType<ToolObserved>().Any()
            ? MindStep.Of(ScriptedMind.Say("One note says October 14; the room said the 21st."), "Possibly disagrees with the stored decision.", ScriptedMind.Verdict(consistent: false))
            : MindStep.Of(ScriptedMind.Tool("read_note", ("projectId", projectId), ("noteId", noteId)), "Reading the stored decision.")));

        s.WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectAttention(Presentation.Result, "Conflict")
            .ExpectNoAttention(Presentation.Alert);
        Assert.Contains("evidence is thin", s.FindTask(TaskKind.Check)!.PresentationReason);
    }

    [Fact]
    public void SomethingTheMindBarelyRatedCannotAlert()
    {
        var mind = new ListeningMind { Significance = 0.58 }.When("21st", line => Check("Check the stated date.", line));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note(Decision);
        var (projectId, noteId) = StoredIn(s, "atlas");
        mind.Works(Conflicting(projectId, noteId));

        // Both sources are opened: the evidence is whole and the mind's own rating of what it heard is what holds the card back.
        s.WithListening().StartListening()
            .Listen("Marketing wants the beta out on the 21st.")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Result)
            .ExpectNoAttention(Presentation.Alert);
        Assert.Contains("confidence 0.58", s.FindTask(TaskKind.Check)!.PresentationReason);
    }

    [Fact]
    public void TheUsersReactionToACardIsRecordedOnTheTask()
    {
        var mind = new ListeningMind().When("21st", line => Check("Check the stated date.", line));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note(Decision);
        var (projectId, noteId) = StoredIn(s, "atlas");
        mind.Works(Conflicting(projectId, noteId));

        s.WithListening().StartListening()
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
