using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.SelfChange;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Core.Usage;
using Relay.Core.Workflows;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The Alpha gate: the README's four proof scenarios as scripted end-to-end tests (CI), plus live
/// counterparts that return without running unless <see cref="LiveModel.FromEnvironment"/> is set
/// (<c>RELAY_LIVE_MODEL_KEY</c> or <c>RELAY_LIVE=1</c>).
/// </summary>
public class AlphaGateTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Contradiction = "Marketing wants the Atlas beta out on the 21st.";
    private const string ResearchAnswer =
        "Lightshift's closest competitors are Deputy and 7shifts. Source: search artifacts and project notes.";

    private static void Listening(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
        s.Listening.Enabled = true;
    }

    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    private static void Researching(RelaySettings s, bool listening = true)
    {
        Mind(s);
        s.Listening.Enabled = listening;
        s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research", SupportsSearch = true });
        s.Search.Enabled = true;
        s.Search.Endpoint = "https://search.test/v1/web/search";
        s.Search.SecretName = "search";
    }

    // ----------------------------------------------------------------------------------------
    // Scenario 1 — messy window → source-linked note/task; correction updates the same work
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Gate1_MessyWindowProducesSourceLinkedWorkAndCorrectionUpdatesIt()
    {
        var mind = new ListeningMind()
            .When("We decided", "remember", "Keep this decision.", note: "Atlas beta ships on October 14.", noteType: "decision", project: "Atlas", topic: "atlas beta")
            .When("21st", "check", "Check the stated Atlas beta date against the stored decision.", project: "Atlas", topic: "atlas beta", mergeKey: "check:atlas:beta-date");

        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")));
        var projectId = s.H.Registry.FindActive("atlas")!.Id;
        mind.Works(AtlasWorking(projectId));

        s.StartListening()
            .Listen(Decision)
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .Listen(Contradiction)
            .ExpectTask(TaskKind.Check, TaskStatus.AwaitingApproval, TaskOrigin.Observed)
            .ExpectAnyProposal(Actions.SupersedeNote, "pending");

        var check = s.FindTask(TaskKind.Check)!;
        Assert.False(check.Consistent);
        Assert.Contains(check.Citations, c => c.Kind == SearchIndex.NoteKind);
        Assert.Contains(check.Citations, c => c.Kind == SearchIndex.ExcerptKind);

        s.Approve(Actions.SupersedeNote)
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.NoteSuperseded);

        var atlas = s.H.Registry.FindActive("atlas")!;
        var notes = ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note).ToList();
        Assert.Equal(NoteStatus.Superseded, Assert.Single(notes, n => n.Body.Contains("October 14")).Status);
        Assert.Equal(NoteStatus.Active, Assert.Single(notes, n => n.Body.Contains("October 21")).Status);

        Assert.DoesNotContain(Decision, s.H.LedgerText());
        Assert.DoesNotContain(Contradiction, s.H.LedgerText());
        AssertLedgerPrivacy(s);
    }

    private static ScriptedMind AtlasWorking(string projectId) => new ScriptedMind().Always(request =>
    {
        var asked = request.Transcript.OfType<InputObserved>().First().Source == InputObserved.Ask;
        var read = request.Transcript.OfType<ToolObserved>().ToList();
        if (read.Count == 0)
            return MindStep.Of(Tool("search", ("query", "Atlas beta ships"), ("project", "atlas"), ("limit", "5")), "Looking up the record.");
        var notes = Found(request);
        if (read.Count - 1 < notes.Count)
            return MindStep.Of(Tool("read_note", ("projectId", projectId), ("noteId", notes[read.Count - 1])), "Reading the record.");
        if (asked)
            return MindStep.Of(Say("We decided the Atlas beta ships on October 21."), "Answering.");
        if (read.All(t => t.Tool != "read_excerpt"))
            return MindStep.Of(Tool("read_excerpt", ("excerptId", request.Transcript.OfType<InputObserved>().First().ExcerptId!)), "Reading what was said.");
        if (!request.Transcript.OfType<PolicyObserved>().Any())
            return MindStep.Of(Propose(Actions.SupersedeNote, "The date heard contradicts the stored decision.",
                    ("projectId", projectId), ("noteId", notes[0]), ("newText", "Atlas beta ships on October 21."),
                    ("type", NoteTypes.Decision), ("sourceExcerptId", request.Transcript.OfType<InputObserved>().First().ExcerptId!),
                    ("spanStart", "0"), ("spanEnd", Contradiction.Length.ToString())),
                "Proposing the update.", Verdict(consistent: false));
        return MindStep.Of(Say("Stored October 14 conflicts with the 21st."), "Verdict.");
    });

    private static List<string> Found(MindRequest request)
    {
        using var hits = JsonDocument.Parse(request.Transcript.OfType<ToolObserved>().First(t => t.Tool == "search").Data!);
        return hits.RootElement.EnumerateArray()
            .Where(h => h.GetProperty("kind").GetString() == SearchIndex.NoteKind)
            .Select(h => (Id: h.GetProperty("id").GetString()!, Current: h.GetProperty("status").GetString() == NoteStatus.Active))
            .OrderByDescending(n => n.Current).Select(n => n.Id).ToList();
    }

    // ----------------------------------------------------------------------------------------
    // Scenario 2 — research with personal context + search; observation continues
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Gate2_ResearchUsesPersonalContextAndSearchWhileObservationContinues()
    {
        var search = new FakeSearchClient().Reply(
            new SearchHitResult("Deputy", "https://example.test/deputy", "Shift scheduling for restaurants."),
            new SearchHitResult("7shifts", "https://example.test/7shifts", "Restaurant workforce platform."));
        var external = new ScriptedModelClient().Reply(ResearchAnswer);
        var mind = new ScriptedMind();

        using var s = Scenario.New(_tmp, st => Researching(st, listening: false), mind: mind, externalClients: _ => external, searchClient: search, inlinePost: false)
            .WithWorkspace()
            .WithSecret("external-research")
            .WithSecret("search")
            .Project("Lightshift")
            .Note("Lightshift is our scheduling app for shift workers in small restaurants.")
            .Note("We decided Lightshift targets independent restaurants first, chains later.")
            .Do("standing search grant", c => Assert.True(c.UpdatePreference("sources.allowOnlineSearch", "true")))
            .ExpectEvent(EventTypes.NoteRouted, atLeast: 2);

        var project = s.H.Registry.FindActive("lightshift")!;
        var noteIds = ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(2, noteIds.Count);
        mind.Always(Gate2Mind(project.Id, noteIds));

        s.WithListening()
            .StartListening()
            .Hear("Anyway, how was the weekend?").Observe()
            .ExpectListening()
            .ExpectNoEvent(EventTypes.ObserveRaised)
            .Ask("research Lightshift's competitors and give me a short plan")
            .ExpectState(RelayState.Ready)
            .Approve(Actions.ModelRequest)
            .PumpUntil("the reply and answer", () => s.ForegroundSettled, TimeSpan.FromSeconds(15))
            .ExpectOutcome("executed")
            .ExpectEvent(EventTypes.ExternalPackaged);

        Assert.Single(external.Requests);
        Assert.Contains("scheduling app for shift workers", external.Requests[0].Messages.Single(m => m.Role == "user").Content);
        var answered = s.Snap.Tasks.FirstOrDefault(t => (t.Answer ?? "").Contains("Deputy", StringComparison.OrdinalIgnoreCase))
            ?? s.Response;
        Assert.Contains("Deputy", answered.Answer ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RelayState.Ready, s.Snap.State);
        // Package contents stay off the external.packaged record (sizes/hashes only).
        var packaged = s.H.Last(EventTypes.ExternalPackaged)!;
        Assert.DoesNotContain("independent restaurants first", packaged.Data.GetRawText());
        AssertLedgerPrivacy(s);
    }

    private static Func<MindRequest, MindStep> Gate2Mind(string projectId, IReadOnlyList<string> notes) => req =>
    {
        if (req.Observing) return MindStep.Of(Wait("ordinary talk"), "Listening.");
        var opened = req.Transcript.OfType<ToolObserved>().Count(t => t.Tool == "read_note");
        return req.Transcript[^1] switch
        {
            DelegateObserved { Stage: DelegateObserved.Returned } returned
                => MindStep.Of(Tool("read_artifact", ("artifactId", returned.ArtifactId!)), "Opening the reply."),
            DelegateObserved { Stage: DelegateObserved.Failed }
                => MindStep.Of(Say("Research failed."), "Reporting."),
            DelegateObserved => MindStep.Of(Wait("the reply is on its way"), "Waiting."),
            ToolObserved { Tool: "read_artifact", Ok: true }
                => MindStep.Of(Say(ResearchAnswer), "Answered."),
            ApprovalObserved { Granted: false }
                => MindStep.Of(Say("Nothing was sent."), "Stopped."),
            _ when opened < notes.Count
                => MindStep.Of(Tool("read_note", ("projectId", projectId), ("noteId", notes[opened])),
                    "Opening a Lightshift note.", Read(0.9, MindRead.NeedExternalReasoning)),
            _ => MindStep.Of(Delegate("research", "Research Lightshift competitors using the notes; you may search.", 4_000, true, [.. notes]),
                "Asking research.", Read(0.9, MindRead.NeedExternalReasoning, MindRead.NeedWorldKnowledge)),
        };
    };

    // ----------------------------------------------------------------------------------------
    // Scenario 3 — repeated friction → approved improvement; reuse and revert
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Gate3_RepeatedFrictionYieldsApprovedReusableRevertibleImprovement()
    {
        var at = Harness.T0;
        var usage = new UsageRecorder(_tmp.Root);
        for (var i = 1; i <= 3; i++)
            usage.Record(new UsageLine(at, "t" + i, InputObserved.Ask, "mind:test", null, 0.4, [MindRead.NeedNewTool], 0.3, 2, 0, 0, 100, 40, 1_000, "answered",
                new Dictionary<string, string>(StringComparer.Ordinal), null, null));

        using (var h = new Harness(_tmp.Root, Mind).Start())
        {
            Assert.True(h.Coordinator.TryReviewFriction("idle"));
            var improve = Assert.Single(h.Snap.Tasks, t => t.Lane == "friction" && t.Kind == TaskKind.Improve);
            var card = Assert.Single(improve.Proposals);
            h.Coordinator.Approve(card.ProposalId);
            Assert.Equal("executed", Assert.Single(h.Snap.Tasks, t => t.TaskId == improve.TaskId).Outcome);
            Assert.Contains(h.Snap.ChangeSets, c => !c.Reverted);
        }

        // Reusable half: a tested workflow is approved, run, and reverted as a change set.
        using var wfRoot = new TempRoot();
        var clock = new FixedClock(Harness.T0);
        var draft = new WorkflowDefinition
        {
            Name = "list_and_say",
            Description = "List projects then say how many there are.",
            Version = 1,
            Steps =
            [
                new WorkflowStep("use_tool", new Dictionary<string, string> { ["name"] = "list_projects" }),
                new WorkflowStep("say", new Dictionary<string, string> { ["text"] = "Here are your projects." }),
            ],
            DraftedAt = Harness.T0,
            BuiltBy = "gate",
        };
        var mind = new ScriptedMind().Always(req =>
        {
            if (req.Transcript[^1] is InputObserved)
            {
                var store = new WorkflowStore(wfRoot.Root, new ChangeSetStore(wfRoot.Root));
                var report = new WorkflowBuilder(store, () => clock.UtcNow).Test(draft, "task");
                Assert.True(report.Passed, report.Summary);
                return MindStep.Of(Propose(Actions.AddWorkflow, "Built after repeated capability friction",
                    ("name", "list_and_say"),
                    ("definitionSha256", report.Package.DefinitionSha256),
                    ("benefit", "Lists projects in one step after repeated new_tool friction."),
                    ("permissions", "Runs read-only tools; no new authority."),
                    ("scope", "Adds workflow list_and_say."),
                    ("acceptance", report.Summary)), "Proposing the workflow");
            }
            if (req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Promoted)
                && !req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Finished))
                return MindStep.Of(RunWorkflow("list_and_say"), "Reusing the workflow");
            if (req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Finished))
                return MindStep.Of(Say("Listed your projects via the workflow."), "Done");
            return MindStep.Of(Say("ok."), "Done");
        });

        using var s = Scenario.New(wfRoot, Mind, mind: mind, clock: clock).WithWorkspace();
        s.Ask("Add a workflow that lists my projects")
            .Approve(Actions.AddWorkflow)
            .PumpUntil("workflow ran", () => s.ForegroundSettled, TimeSpan.FromSeconds(10))
            .ExpectEvent(EventTypes.WorkflowPromoted)
            .ExpectEvent(EventTypes.WorkflowRan);

        var changeSet = s.H.ChangeSets.All().Single(c => c.Kind == ChangeKinds.Workflow && !c.Reverted);
        Assert.True(s.H.ChangeSets.Revert(changeSet.ChangeSetId, "not wanted", clock.UtcNow).Ok);
        Assert.False(s.H.Workflows.Store.IsPromoted("list_and_say"));
        AssertLedgerPrivacy(s);
    }

    // ----------------------------------------------------------------------------------------
    // Scenario 4 — wait / fail / resume / cancel without losing objective or blocking others
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Gate4_WaitFailResumeCancelWithoutLosingObjectiveOrBlockingOthers()
    {
        // Cancel is final; late completions are no-ops.
        var cancelMind = new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.CreateProject, "You asked for it", ("name", "Atlas")), "Proposing", Read(0.2))
            : MindStep.Of(Say("done"), "Done"));
        using (var s = Scenario.New(_tmp, Mind, mind: cancelMind).WithWorkspace()
            .Command("create a project called Atlas")
            .Cancel()
            .ExpectEvent(EventTypes.TaskCancelled))
        {
            var proposalId = s.H.Last(EventTypes.ProposalReceived)!.DataString("proposalId")!;
            s.H.Coordinator.CompletePendingOperation(proposalId, Relay.Core.Execution.ExecutionResult.Ok("late"));
            Assert.Equal(0, s.H.Count(EventTypes.ExecutionCompleted));
            Assert.Equal(TaskStatus.Cancelled, s.Snap.Response?.Status);
        }

        // Resume after restart keeps the objective.
        using var tmp2 = new TempRoot();
        var resumeMind = new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.CreateProject, "You asked for it", ("name", "ResumeMe")), "Proposing", Read(0.2))
            : MindStep.Of(Say("ResumeMe is ready."), "Done"));
        using var resumed = Scenario.New(tmp2, Mind, mind: resumeMind).WithWorkspace()
            .Command("create ResumeMe")
            .CrashAndRestart()
            .ExpectState(RelayState.Ready);
        Assert.Equal(true, resumed.H.Last(EventTypes.TaskInterruptedFound)!.DataBool("resumed"));
        var waiting = Assert.Single(resumed.Snap.LiveTasks, t => t.Status == TaskStatus.AwaitingApproval);
        Assert.Contains("ResumeMe", waiting.Instruction ?? waiting.Title ?? "", StringComparison.OrdinalIgnoreCase);
        resumed.Approve(Actions.CreateProject).ExpectOutcome("executed").ExpectProject("resumeme");

        // Unrelated work is not blocked: observation continues while a raised task waits.
        using var tmp3 = new TempRoot();
        var raises = 0;
        var observeMind = new ScriptedMind().Always(req =>
        {
            if (!req.Observing) return MindStep.Of(Propose(Actions.CreateProject, "home for the decision", ("name", "Atlas" + req.TaskId[^4..])), "Asking.");
            if (req.StepIndex > 0) return MindStep.Of(Wait("nothing further"), "Listening.");
            var label = "#" + ++raises;
            return MindStep.Of(Raise("organize", $"File what was said in line {label}", label), "Raising.", new MindRead("a conversation", 0.3, [MindRead.NeedNone], 0.8, 0, RiskRead.None));
        });
        using var obs = Scenario.New(tmp3, Listening, mind: observeMind).WithWorkspace()
            .StartListening()
            .Hear(Decision).Observe()
            .ExpectTaskCount(1)
            .Hear(Contradiction).Observe()
            .ExpectListening()
            .ExpectTaskCount(2);
        Assert.Contains(obs.Snap.Tasks, t => t.Status == TaskStatus.AwaitingApproval);
        Assert.Equal(RelayState.Ready, obs.Snap.State);
        AssertLedgerPrivacy(obs);
    }

    // ----------------------------------------------------------------------------------------
    // Live counterparts (skip without a local model)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void LiveGate1_MessyWindowAgainstLocalModel()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); cfg.Listening.Enabled = true; }, mind: new ModelMind(client)).WithWorkspace();
        s.Do("create Atlas", c => Assert.True(c.CreateProject("Atlas")))
            .StartListening()
            .Hear(Decision).Observe()
            .PumpUntil("raise or check", () => s.H.Count(EventTypes.ObserveRaised) > 0 || s.H.Count(EventTypes.ObserveChecked) > 0, TimeSpan.FromSeconds(180));
        live.Write("gate1-live.txt", s.Transcript());
        Assert.True(s.H.Count(EventTypes.ObserveRaised) + s.H.Count(EventTypes.ObserveChecked) > 0);
        Assert.DoesNotContain("October 14", s.H.LedgerText());
    }

    [Fact]
    public void LiveGate2_ResearchAgainstLocalModel()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        var external = new ScriptedModelClient().Reply(ResearchAnswer);
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); Researching(cfg, listening: false); }, mind: new ModelMind(client), externalClients: _ => external, inlinePost: false)
            .WithWorkspace().WithSecret("external-research").WithSecret("search")
            .Project("Lightshift")
            .Note("Lightshift is our scheduling app for shift workers.")
            .Do("grant search", c => Assert.True(c.UpdatePreference("sources.allowOnlineSearch", "true")));
        s.Ask("research Lightshift competitors briefly")
            .PumpUntil("approval or answer", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(240));
        live.Write("gate2-live.txt", s.Transcript());
        Assert.Equal(RelayState.Ready, s.Snap.State);
    }

    [Fact]
    public void LiveGate3_FrictionReviewAgainstLocalModel()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        // Friction review itself needs no model; live gate just confirms the session can host it with a real mind present.
        var at = Harness.T0;
        var usage = new UsageRecorder(_tmp.Root);
        for (var i = 1; i <= 3; i++)
            usage.Record(new UsageLine(at, "l" + i, InputObserved.Ask, "mind:live", null, 0.4, [MindRead.NeedNewTool], 0.3, 2, 0, 0, 100, 40, 1_000, "answered",
                new Dictionary<string, string>(StringComparer.Ordinal), null, null));
        using var client = live.Client();
        using var h = new Harness(_tmp.Root, live.Configure, mind: new ModelMind(client)).Start();
        Assert.True(h.Coordinator.TryReviewFriction("idle"));
        Assert.NotNull(h.Last(EventTypes.FrictionReviewed));
        live.Write("gate3-live.txt", "friction.reviewed proposed=" + h.Last(EventTypes.FrictionReviewed)!.DataBool("proposed"));
    }

    [Fact]
    public void LiveGate4_CancelAndResumeAgainstLocalModel()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        using var s = Scenario.New(_tmp, live.Configure, mind: new ModelMind(client)).WithWorkspace();
        s.Ask("create a project called LiveCancel")
            .PumpUntil("proposal or settle", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(180));
        if (s.Snap.PendingProposals.Any())
            s.Cancel().ExpectEvent(EventTypes.TaskCancelled);
        live.Write("gate4-live.txt", s.Transcript());
        Assert.Equal(RelayState.Ready, s.Snap.State);
    }

    /// <summary>Ledger privacy invariants shared by every gate scenario.</summary>
    private static void AssertLedgerPrivacy(Scenario s)
    {
        var text = s.H.LedgerText();
        // Tool packages must never land verbatim in the ledger.
        Assert.DoesNotContain("function run(", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sourceCode\"", text, StringComparison.OrdinalIgnoreCase);
    }
}
