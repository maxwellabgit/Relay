using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The mind listening (the Alpha, step 1): one loop per conversation, one pass per stretch of talk, and one
/// move the mind only has here — <c>raise</c>, which hands work to a task of its own. What these tests hold
/// Relay to: ordinary talk costs nothing, what matters becomes a task with the words that substantiate it,
/// the observing loop never waits on what it raised, and the ledger still holds no words of the room.
/// </summary>
public class ObservingTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ObservingTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Chatter = "Anyway, how was the weekend, did you get out at all?";
    private const string Launch = "Marketing wants the launch email out a week before.";

    /// <summary>Mind mode with the note chord listening, which is all listening now needs: a mind and the setting on.</summary>
    private static void Listening(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
        s.Listening.Enabled = true;
    }

    /// <summary>A read whose significance clears the raise bar, so a scripted raise is not refused for being trivial.</summary>
    private static MindRead Matters(double significance = 0.8) => new("a conversation", 0.3, [MindRead.NeedNone], significance, 0, RiskRead.None);

    // ----------------------------------------------------------------------------------------
    // Units: the pass
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void APassShowsTheMindLabelledLinesAndTheUsualMoveIsWait()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Wait("just chatter"), "Nothing to do with that."));
        var loop = NewLoop(mind, out var host);
        var pass = loop.ObserveAsync(Window(("#1", "S1", Chatter)), CancellationToken.None).Result;

        Assert.Equal(1, pass.Moves);
        Assert.Equal(0, pass.Raises);
        Assert.Null(pass.Error);
        Assert.Empty(host.Raised);
        // The mind saw the line with its label; a raise can only name what it was shown.
        Assert.Contains("#1", MindPrompt.Transcript(mind.Requests[0]));
        Assert.Contains(Chatter, MindPrompt.Transcript(mind.Requests[0]));
        Assert.True(mind.Requests[0].Observing);
    }

    [Fact]
    public void OnlyTheMovesOfListeningAreAcceptedAndTheRestPointAtRaise()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Propose(Relay.Core.Policy.Actions.CreateProject, "we need one", ("name", "Atlas")), "Making a project.", Matters())
            .Step(ScriptedMind.Wait("understood"), "Listening on.");
        var loop = NewLoop(mind, out _);
        loop.ObserveAsync(Window(("#1", "S1", Decision)), CancellationToken.None).Wait();

        var told = loop.Transcript.OfType<SystemObserved>().Last().Text;
        Assert.Contains("not a move you may make while listening", told);
        Assert.Contains("Raise the work", told);
    }

    [Fact]
    public void ARaiseNamingALineThatWasNeverShownIsRefusedWithTheLabelsExplained()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Raise("check", "Confirm the Atlas beta date with marketing", "#9"), "Raising the date.", Matters())
            .Step(ScriptedMind.Wait("understood"), "Listening on.");
        var loop = NewLoop(mind, out var host);
        loop.ObserveAsync(Window(("#1", "S1", Decision)), CancellationToken.None).Wait();

        Assert.Empty(host.Raised);
        var refused = loop.Transcript.OfType<RaisedObserved>().Last();
        Assert.NotNull(refused.Refused);
        Assert.Contains("name no line of this conversation", refused.Refused);
        Assert.Contains("#1", refused.Refused);
    }

    [Fact]
    public void ARaiseBelowTheSignificanceBarIsRefusedAndTheMindIsToldTheNumber()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Raise("check", "Look into what they said about the weekend", "#1"), "Raising it.", Matters(significance: 0.1))
            .Step(ScriptedMind.Wait("fair enough"), "Listening on.");
        var loop = NewLoop(mind, out var host);
        loop.ObserveAsync(Window(("#1", "S1", Chatter)), CancellationToken.None).Wait();

        Assert.Empty(host.Raised);
        var refused = Assert.Single(loop.Transcript.OfType<RaisedObserved>());
        Assert.Contains("below the bar for spending the user's attention", refused.Refused);
        Assert.Equal(Decider.Ignore, loop.Decisions.Last(d => d.Name == Decider.Raise).Outcome);
    }

    [Fact]
    public void TheSameObjectiveRaisedTwiceInOneConversationIsRefused()
    {
        var raise = ScriptedMind.Raise("check", "Confirm the Atlas beta date with marketing", "#1");
        var mind = new ScriptedMind()
            .Step(raise, "Raising the date.", Matters())
            .Step(ScriptedMind.Wait("done"), "Listening on.")
            .Step(raise, "Raising the date.", Matters())
            .Step(ScriptedMind.Wait("understood"), "Listening on.");
        var loop = NewLoop(mind, out var host);
        loop.ObserveAsync(Window(("#1", "S1", Decision)), CancellationToken.None).Wait();
        loop.ObserveAsync(Window(("#2", "S2", Launch)), CancellationToken.None).Wait();

        Assert.Single(host.Raised);
        Assert.Contains("already raised in this conversation", loop.Transcript.OfType<RaisedObserved>().Last().Refused);
    }

    [Fact]
    public void AFailedPassDoesNotEndTheConversationAndTheNextOneRunsClean()
    {
        var mind = new ScriptedMind()
            .Fail("Model unavailable: connection refused")
            .Fail("Model unavailable: connection refused")
            .Step(ScriptedMind.Wait("nothing here"), "Listening on.");
        var loop = NewLoop(mind, out _);
        var first = loop.ObserveAsync(Window(("#1", "S1", Chatter)), CancellationToken.None).Result;
        var second = loop.ObserveAsync(Window(("#2", "S2", Launch)), CancellationToken.None).Result;

        Assert.NotNull(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(1, second.Moves);
    }

    [Fact]
    public void TheTranscriptIsAWindowSoALongConversationDoesNotGrowWithoutEnd()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Wait("chatter"), "Listening."));
        var loop = NewLoop(mind, out _, new ObservingBudget(MaxTranscript: 6));
        for (var i = 0; i < 20; i++) loop.ObserveAsync(Window(("#" + (i + 1), "S" + i, Chatter)), CancellationToken.None).Wait();

        Assert.Equal(20, loop.Passes);
        Assert.True(loop.Transcript.Count <= 6, $"the transcript held {loop.Transcript.Count} observations");
        // The latest talk is always the thing the mind sees last.
        Assert.IsType<MoveObserved>(loop.Transcript[^1]);
    }

    // ----------------------------------------------------------------------------------------
    // End to end through the coordinator
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void OrdinaryTalkIsWaitedThroughAndNothingIsKept()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Wait("ordinary talk"), "Nothing worth keeping."));
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .StartListening()
            .Hear(Chatter).Observe()
            .ExpectListening()
            .ExpectTaskCount(0)
            .ExpectExcerpts(0)
            .ExpectEvent(EventTypes.ObserveChecked)
            .ExpectNoEvent(EventTypes.ObserveRaised)
            .StopListening()
            .ExpectState(RelayState.Completed);
        _output.WriteLine(s.Transcript());

        // The mind read the stream, and every request it took was a listening pass.
        Assert.Equal("mind:scripted", s.H.Last(EventTypes.StreamStarted)!.DataString("mind"));
        Assert.True(mind.Requests.All(r => r.Observing));
    }

    [Fact]
    public void ADecisionHeardIsRaisedAsATaskWithTheLinesThatSubstantiateIt()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Raise("check", "Confirm with marketing that the Atlas beta ships on October 14", "#1"), "Raising the beta date.", Matters())
            .Always(_ => MindStep.Of(ScriptedMind.Wait("nothing further"), "Listening on."));
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .StartListening()
            .Hear(Decision).Observe()
            .ExpectTask(TaskKind.Check, origin: TaskOrigin.Observed)
            .ExpectExcerpts(1)
            .ExpectEvent(EventTypes.ObserveRaised)
            .ExpectEvent(EventTypes.ExcerptStored);
        _output.WriteLine(s.Transcript());

        // The excerpt reproduces the exact words of the line the mind named.
        var excerpt = Assert.Single(s.H.Excerpts.All());
        Assert.Equal(Decision, excerpt.Text);
        Assert.Equal("mind:scripted", excerpt.SelectedBy);
        var raised = s.H.Last(EventTypes.ObserveRaised)!;
        Assert.Equal("check", raised.DataString("kind"));
        Assert.Equal("mind:scripted", raised.DataString("by"));
        // The objective is the mind's own sentence, and it is what the task was given.
        var task = s.FindTask(TaskKind.Check)!;
        Assert.Contains("October 14", task.Title ?? "");
    }

    [Fact]
    public void ARaiseWhoseWholeContentIsANoteFilesItWithoutAPlanningTurn()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Raise("remember", "Keep the Atlas beta ship date", "#1", note: "The Atlas beta ships on October 14.", noteType: "decision", project: "Atlas"),
                "Keeping the ship date.", Matters(0.9))
            .Always(_ => MindStep.Of(ScriptedMind.Wait("nothing further"), "Listening on."));
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")))
            .StartListening()
            .Hear(Decision).Observe()
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.NoteDraftCreated)
            .ExpectEvent(EventTypes.NoteRouted);
        _output.WriteLine(s.Transcript());

        // The mind wrote the note on the listening pass; the raised task needed no step of its own.
        Assert.True(mind.Requests.All(r => r.Observing));
        var atlas = s.H.Registry.FindActive("atlas")!;
        var filed = Assert.Single(Relay.Core.Notes.ProjectNoteStore.ReadAll(atlas.RootPath).Notes).Note;
        Assert.Equal("The Atlas beta ships on October 14.", filed.Body);
        Assert.Equal("decision", filed.Type);
    }

    [Fact]
    public void ObservationContinuesWhileTheTaskItRaisedIsWaitingForTheUser()
    {
        var raises = 0;
        var mind = new ScriptedMind().Always(req =>
        {
            // The raised task: a proposal that stops for the user's approval and never comes back on its own.
            if (!req.Observing) return MindStep.Of(ScriptedMind.Propose(Relay.Core.Policy.Actions.CreateProject, "the decision needs a home", ("name", "Atlas" + req.TaskId[^4..])), "Asking about a project.");
            // Listening: one thing raised on the first move of each pass, nothing after it.
            if (req.StepIndex > 0) return MindStep.Of(ScriptedMind.Wait("nothing further"), "Listening on.");
            var label = "#" + ++raises;
            return MindStep.Of(ScriptedMind.Raise("organize", $"File what was said in line {label} of the conversation", label), "Raising it.", Matters());
        });
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .StartListening()
            .Hear(Decision).Observe()
            .ExpectTaskCount(1)
            .Hear(Launch).Observe()
            .ExpectListening()
            .ExpectTaskCount(2);
        _output.WriteLine(s.Transcript());

        // The conversation was still being read while the first task sat on the user's approval: that is the point.
        Assert.Contains(s.Snap.Tasks, t => t.Status == TaskStatus.AwaitingApproval);
        Assert.True(s.Snap.Listening!.Passes >= 2, $"{s.Snap.Listening.Passes} pass(es)");
        Assert.Equal(2, s.H.Count(EventTypes.ObserveRaised));
        Assert.Equal(2, s.Snap.PendingProposals.Count());
    }

    [Fact]
    public void TheMindMayCheckWhatRelayHoldsBeforeRaising()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Tool("list_projects"), "Checking which project that is.", Matters())
            .Then(req =>
            {
                var tool = Assert.IsType<ToolObserved>(req.Transcript[^1]);
                Assert.True(tool.Ok, tool.Summary);
                return MindStep.Of(ScriptedMind.Raise("remember", "Keep the Atlas beta ship date", "#1", note: "The Atlas beta ships on October 14.", noteType: "decision", project: "Atlas"),
                    "Keeping it under Atlas.", Matters(0.9));
            })
            .Always(_ => MindStep.Of(ScriptedMind.Wait("nothing further"), "Listening on."));
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")))
            .StartListening()
            .Hear(Decision).Observe()
            .ExpectEvent(EventTypes.ToolCalled)
            .ExpectEvent(EventTypes.ObserveRaised);
        _output.WriteLine(s.Transcript());

        // The call belongs to the conversation, not to a task, and its arguments are recorded as fingerprints.
        var called = s.H.Last(EventTypes.ToolCalled)!;
        Assert.Equal("list_projects", called.DataString("tool"));
        Assert.Null(called.DataString("taskId"));
        Assert.NotNull(called.DataString("streamId"));
    }

    [Fact]
    public void NoWordsOfTheConversationReachTheLedgerOnTheMindsPath()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Raise("check", "Confirm the beta ship date with marketing", "#1"), "Raising the beta date.", Matters())
            .Always(_ => MindStep.Of(ScriptedMind.Wait("nothing further"), "Listening on."));
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .StartListening()
            .Hear(Decision).Observe()
            .StopListening();
        _output.WriteLine(s.Transcript());

        var ledger = s.H.LedgerText();
        Assert.DoesNotContain("October 14", ledger);
        Assert.DoesNotContain("weekend", ledger);
        Assert.Contains("withheld:", ledger);
        // The words are in the excerpt on disk, under the retention the guard sets.
        Assert.Contains("October 14", Assert.Single(s.H.Excerpts.All()).Text);
    }

    [Fact]
    public void MindIsThePipelineByDefault()
    {
        Assert.Equal(OrchestratorSettings.Mind, new RelaySettings().Orchestrator.Mode);
        Assert.Empty(new RelaySettings().Validate());
    }

    [Fact]
    public void TheDefaultWithNoModelSaysRelayHasNoMindInsteadOfQuietlyFallingBack()
    {
        // No mind is supplied and the model is off: the default mode is still mind, and Relay says what that costs.
        using (var s = Scenario.New(_tmp).WithWorkspace().ExpectEvent(EventTypes.MindUnavailable))
        {
            _output.WriteLine(s.Transcript());
            var notice = Assert.Single(s.Snap.Review, r => r.Kind == ReviewItemKind.MindUnavailable);
            Assert.Contains("switched off in Settings", notice.Detail);
            Assert.Equal("disabled", s.H.Last(EventTypes.MindUnavailable)!.DataString("reason"));
        }

        // With a mind in place there is nothing to say.
        using var second = new TempRoot();
        using var ok = Scenario.New(second, Listening, mind: new ScriptedMind()).WithWorkspace();
        Assert.DoesNotContain(ok.Snap.Review, r => r.Kind == ReviewItemKind.MindUnavailable);
        Assert.Equal(0, ok.H.Count(EventTypes.MindUnavailable));
    }

    /// <summary>A mind in place is not enough: with listening off the note chord still dictates and nothing is read.</summary>
    [Fact]
    public void WithListeningOffTheNoteChordStillDictatesANote()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Wait("not used"), "Not used."));
        using var s = Scenario.New(_tmp, x => { x.Orchestrator.Mode = OrchestratorSettings.Mind; x.Model.Enabled = true; }, mind: mind).WithWorkspace()
            .Note("Remember that the Atlas beta ships on October 14.")
            .ExpectListening(false);
        _output.WriteLine(s.Transcript());

        Assert.Equal(0, s.H.Count(EventTypes.StreamStarted));
    }

    // ----------------------------------------------------------------------------------------
    // Support
    // ----------------------------------------------------------------------------------------

    private ObservingLoop NewLoop(IMind mind, out RecordingObserveHost host, ObservingBudget? budget = null)
    {
        host = new RecordingObserveHost();
        return new ObservingLoop("stream-1", mind, host, new MindContext { Projects = ["Atlas (id p1, slug atlas)"] },
            new Decider(DecisionSet.Default()), budget, new FixedClock(Harness.T0));
    }

    private static WindowObserved Window(params (string Label, string SegmentId, string Text)[] lines)
        => new(Harness.T0, "stream-1", lines.Select(l => new WindowLine(l.Label, l.SegmentId, l.Text)).ToList(), [], lines.Length * 3);

    private sealed class RecordingObserveHost : IObservingHost
    {
        public List<RaiseMove> Raised { get; } = new();
        public List<UseToolMove> Tools { get; } = new();
        public List<string> Lines { get; } = new();
        public List<string> Refusals { get; } = new();

        public void Stepped(ObservingLoop loop, MindStep step) { }
        public void Waited(ObservingLoop loop, WaitMove move) { }
        public void Said(ObservingLoop loop, SayMove move) => Lines.Add(move.Text);
        public void Refused(ObservingLoop loop, RaiseMove move, string reason) => Refusals.Add(reason);

        public Task<MoveOutcome> UseToolAsync(ObservingLoop loop, UseToolMove move, CancellationToken cancellationToken)
        {
            Tools.Add(move);
            return Task.FromResult(MoveOutcome.Of(new ToolObserved(Harness.T0, move.Tool, move.Args, true, "1 project", null, ["p1"])));
        }

        public Task<MoveOutcome> RaiseAsync(ObservingLoop loop, RaiseMove move, IReadOnlyList<WindowLine> lines, DecisionRecord raise, CancellationToken cancellationToken)
        {
            Raised.Add(move);
            return Task.FromResult(MoveOutcome.Of(new RaisedObserved(Harness.T0, "task-" + Raised.Count, move.Kind, move.Objective, "excerpt-" + Raised.Count)));
        }
    }

    public void Dispose() => _tmp.Dispose();
}
