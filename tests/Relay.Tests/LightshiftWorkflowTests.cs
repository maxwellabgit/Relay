using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The README's Lightshift workflow: "research Lightshift's competitors and give me an implementation plan".
/// The work goes to a named external model, and everything about that is held here. The mind opens the project
/// notes the question stands on and writes the prompt itself; the package that carries them needs the user's
/// approval, so nothing leaves the machine until it is granted; the reply is stored as a source artifact the
/// answer is built from and cites; the findings are kept as a separate draft note pointing at that artifact;
/// and whether online search is part of the package is a permission, not the mind's to assume. The external
/// model and the mind are scripted; everything between them is the production path.
/// </summary>
public class LightshiftWorkflowTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private const string Answer =
        "Lightshift's closest competitors are Deputy, When I Work, and 7shifts; all three sell shift scheduling to small restaurants and retail. " +
        "Deputy leads on integrations, 7shifts on restaurant-specific features such as tip pooling, When I Work on price. " +
        "Homebase and Sling compete at the free tier with limited scheduling and no payroll export, which matters for independents with fewer than twenty staff. " +
        "None of the five offers a template library for recurring restaurant shift patterns, and only 7shifts handles tip pooling natively. " +
        "Implementation plan: 1. Ship a template library for restaurant shift patterns. 2. Add tip pooling before the beta. 3. Publish a public price page under When I Work. " +
        "4. Offer a payroll CSV export in the first release so independents can leave the free tier of Homebase. 5. Defer chain features until the independent segment converts. " +
        "The plan assumes the independent-restaurant focus recorded in the notes; a chain-first strategy would reorder steps 3 and 5. " +
        "I could not determine current pricing for 7shifts' enterprise tier, and market share figures are unverified.";

    /// <summary>The prompt the mind writes for the external model. It is the whole of the objective: the package carries this and the sources, nothing else.</summary>
    private const string Prompt =
        "Research Lightshift's competitors in shift scheduling for small restaurants and give an implementation plan. " +
        "Work from the sources below and say what you could not determine.";

    /// <summary>The reference note the findings are kept in: short, and traceable to the artifact through the proposal's target.</summary>
    private const string Findings =
        "Lightshift's competitors are Deputy, When I Work, and 7shifts, with Homebase and Sling at the free tier. " +
        "None of them offers a template library for recurring restaurant shift patterns.";

    /// <summary>
    /// The mind runs every task and may hand work to one external profile. <paramref name="profile"/> is false only
    /// where the subject is having nowhere to delegate to; <paramref name="search"/> is what that profile's host can do,
    /// which is configuration and so known before any card is shown.
    /// </summary>
    private static void Delegating(RelaySettings s, bool profile = true, bool search = true, bool listening = false)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
        s.Listening.Enabled = listening;
        if (profile)
        {
            s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research", SupportsSearch = search });
            if (search)
            {
                s.Search.Enabled = true;
                s.Search.Endpoint = "https://search.test/v1/web/search";
                s.Search.SecretName = "search";
            }
        }
    }

    /// <summary>
    /// A Lightshift project with the two notes a research request stands on, and a research model waiting to answer.
    /// The world is built through the Projects panel and the note chord, so nothing in the setup depends on the mind;
    /// the script is attached afterwards, when there are notes for it to name.
    /// </summary>
    private static (Scenario S, ScriptedModelClient Model) Seeded(TempRoot tmp, ScriptedMind mind, ScriptedModelClient? model = null, bool search = true)
    {
        var client = model ?? new ScriptedModelClient().Reply(Answer);
        var s = Scenario.New(tmp, st => Delegating(st, search: search), externalClients: _ => client, inlinePost: false, mind: mind).WithWorkspace()
            .Project("Lightshift")
            .Note("Lightshift is our scheduling app for shift workers in small restaurants.")
            .Note("We decided Lightshift targets independent restaurants first, chains later.")
            .ExpectEvent(EventTypes.NoteRouted, atLeast: 2);
        var project = s.H.Registry.FindActive("lightshift")!;
        var notes = ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note).OrderBy(n => n.Created).Select(n => n.Id).ToList();
        mind.Always(Researching(project.Id, notes, search));
        return (s, client);
    }

    /// <summary>
    /// What the mind does with a research request. It opens every note it means to send, so what leaves the machine is
    /// only ever something it looked at, and then delegates — which is where the user is asked. When the reply is back it
    /// opens the stored artifact, so the answer stands on something Relay holds and cites it, keeps the findings as a
    /// reference note pointing at that artifact, and answers with a summary that repeats the reply's own limits.
    /// Scripted so the package, the approval and the artifact are what these tests watch.
    /// </summary>
    private static Func<MindRequest, MindStep> Researching(string projectId, IReadOnlyList<string> notes, bool search) => request =>
    {
        var opened = request.Transcript.OfType<ToolObserved>().Count(t => t.Tool == "read_note");
        return request.Transcript[^1] switch
        {
            DelegateObserved { Stage: DelegateObserved.Returned } returned
                => MindStep.Of(Tool("read_artifact", ("artifactId", returned.ArtifactId!)), "Opening the reply research sent back."),
            DelegateObserved { Stage: DelegateObserved.Failed }
                => MindStep.Of(Say("Research could not be reached, so nothing came back and I have kept nothing."), "Reporting that research was unavailable."),
            DelegateObserved => MindStep.Of(Wait("the reply is on its way"), "Research is working."),
            ToolObserved { Tool: "read_artifact", Ok: true }
                => MindStep.Of(Propose(Actions.CreateDraftNote, "Findings from an approved external task are worth keeping, traceable to the artifact they came from.",
                        ("text", Findings), ("type", NoteTypes.Reference), ("sourceArtifactId", Artifact(request))),
                    "Keeping the findings as a reference note."),
            ExecutionObserved { Action: Actions.CreateDraftNote, Ok: true }
                => MindStep.Of(Say(Summary(request)), "Answered from what research established."),
            ApprovalObserved { Granted: false }
                => MindStep.Of(Say("Nothing was sent. What the notes hold is all I have about Lightshift's competitors."), "Nothing left the machine."),
            PolicyObserved { Outcome: PolicyObserved.Denied } denied
                => MindStep.Of(Say("I cannot send anything out from this: " + string.Join(" ", denied.Reasons)), "Reporting what policy refused."),
            SystemObserved told when told.Text.StartsWith("No delegate profile", StringComparison.Ordinal)
                => MindStep.Of(Say("The notes say Lightshift targets independent restaurants first. Who its competitors are is not in them, and there is no model here that could settle it."),
                    "Answering with the gap named."),
            _ when opened < notes.Count
                => MindStep.Of(Tool("read_note", ("projectId", projectId), ("noteId", notes[opened])), "Opening a Lightshift note to send with the question.",
                    Read(0.9, MindRead.NeedExternalReasoning)),
            _ => MindStep.Of(Delegate("research", Prompt, 4_000, search, [.. notes]), "Asking research, with both notes and nothing else.",
                    Read(0.9, MindRead.NeedExternalReasoning)),
        };
    };

    /// <summary>The artifact the reply was stored as: what the answer cites and the findings note points at.</summary>
    private static string Artifact(MindRequest request)
        => request.Transcript.OfType<DelegateObserved>().Last(d => d.Stage == DelegateObserved.Returned).ArtifactId!;

    /// <summary>The answer: shorter than the reply, the reply's own limits named, the artifact behind it.</summary>
    private static string Summary(MindRequest request)
        => "Deputy, When I Work, and 7shifts are the closest competitors, and none of them has a shift-pattern template library. " +
           "Limits: research could not determine 7shifts' enterprise pricing, and its market share figures are unverified. " +
           "Source: artifact " + Artifact(request) + ".";

    [Fact]
    public void TheRequestNamesWhatIsMissingAndProposesOnePackageBoundToWhatTheMindOpened()
    {
        var mind = new ScriptedMind();
        var (s, model) = Seeded(_tmp, mind);
        using var _ = s;
        s.Command("research Lightshift's competitors and give me an implementation plan").ExpectState(RelayState.Ready /*was AwaitingApproval*/);

        var task = s.Response;
        Assert.Equal(mind.Name, task.Producer);
        // Two axes of the same gap: what the notes do not hold, and that settling it is beyond what runs here.
        var stepped = s.H.Records().First(r => r.Type == EventTypes.MindStepped && r.DataString("taskId") == task.TaskId);
        Assert.Contains(stepped.Data.GetProperty("read").GetProperty("needs").EnumerateArray(), need => need.GetString() == MindRead.NeedExternalReasoning);
        var route = s.H.Records().First(r => r.Type == EventTypes.DecisionMade && r.DataString("decision") == Decider.Route);
        Assert.Equal(Decider.OfferDelegate, route.DataString("outcome"));
        Assert.Contains(task.Steps, step => step.Contains("both notes"));

        // Only what the mind opened may go: the two notes are its citations and exactly the references on the card.
        Assert.Equal(2, task.Citations.Count);
        var proposal = Assert.Single(task.Proposals);
        Assert.Equal(Actions.ModelRequest, proposal.Action);
        Assert.Equal("pending", proposal.Status);
        Assert.Equal("research", proposal.Target["profile"]);
        Assert.Equal("true", proposal.Target["allowSearch"]);
        Assert.Equal("4000", proposal.Target["budgetTokens"]);
        var refs = proposal.Target["refs"].Split(',');
        Assert.Equal(2, refs.Length);
        Assert.All(refs, r => Assert.Contains(r, task.Citations.Select(c => c.Id)));
        Assert.Contains("References leaving the machine:", proposal.Detail);
        Assert.Contains("online search: true", proposal.Detail);
        Assert.Contains(proposal.Reasons, r => r.Contains("not granted by preference") && r.Contains("this task only"));
        Assert.Empty(model.Requests);                                              // nothing has left the machine
        s.ExpectNoEvent(EventTypes.ExternalPackaged);
    }

    [Fact]
    public void ApprovingSendsExactlyThePackageStoresTheArtifactAndAnswersWithItsLimits()
    {
        var mind = new ScriptedMind();
        var (s, model) = Seeded(_tmp, mind);
        using var _ = s;
        s.Command("research Lightshift's competitors and give me an implementation plan").Approve(Actions.ModelRequest)
            .ExpectEvent(EventTypes.ExternalPackaged)                                // recorded before anything is sent
            .PumpUntil("the reply, the artifact and the answer", () => s.ForegroundSettled)
            .ExpectState(RelayState.Ready).ExpectOutcome("executed");

        // Exactly the package: the prompt the mind wrote plus the two notes it opened, nothing else.
        var request = Assert.Single(model.Requests);
        var user = request.Messages.Single(m => m.Role == "user").Content;
        Assert.Contains("Research Lightshift's competitors in shift scheduling", user);
        Assert.Contains("scheduling app for shift workers", user);
        Assert.Contains("independent restaurants first", user);
        Assert.Contains("You may search online", user);
        Assert.Equal("gpt-5-nano", request.Model);
        Assert.Equal(4000, request.MaxOutputTokens);

        var packaged = s.H.Last(EventTypes.ExternalPackaged)!;
        Assert.Equal(2, packaged.DataInt64("sources"));
        Assert.Equal("research", packaged.DataString("profile"));
        Assert.True(packaged.DataBool("allowSearch"));
        Assert.NotNull(packaged.DataString("sha256"));
        // The ledger holds sizes, tokens, timings and destinations — never the words that were sent.
        foreach (var r in s.H.Records().Where(r => r.Type is EventTypes.ExternalPackaged or EventTypes.ExternalResponded or EventTypes.ArtifactStored))
            Assert.DoesNotContain("shift workers", JsonSerializer.Serialize(r.Data));
        // The package on disk is the full text, for the user to inspect what left.
        var package = s.H.External!.ReadPackage(packaged.DataString("packageId")!)!;
        Assert.Equal(2, package.Sources.Count);
        Assert.Contains(package.Sources, src => src.Text.Contains("shift workers"));
        Assert.Equal(package.Chars, package.Objective.Length + package.Sources.Sum(src => src.Text.Length));

        var responded = s.H.Last(EventTypes.ExternalResponded)!;
        Assert.True(responded.DataBool("ok"));
        Assert.Equal(100, responded.DataInt64("promptTokens"));
        Assert.Equal(20, responded.DataInt64("completionTokens"));
        var stored = s.H.Last(EventTypes.ArtifactStored)!;
        var artifactId = stored.DataString("artifactId")!;
        Assert.Equal(Answer, s.H.External.ReadArtifact(artifactId));

        // The answer: concise, the limits named, the artifact cited, and the findings kept as a separate draft note.
        var task = s.Response;
        Assert.NotNull(task.Answer);
        Assert.True(task.Answer!.Length < Answer.Length, "the answer must be shorter than the reply");
        Assert.Contains("Deputy, When I Work, and 7shifts", task.Answer);
        Assert.Contains("could not determine 7shifts' enterprise pricing", task.Answer);
        Assert.Contains(artifactId, task.Answer);
        var citation = Assert.Single(task.Citations, c => c.Kind == SearchIndex.ArtifactKind);
        Assert.Equal(artifactId, citation.Id);
        var note = Assert.Single(task.Proposals, p => p.Action == Actions.CreateDraftNote);
        Assert.Equal("executed", note.Status);                                     // staging write: automatic
        Assert.Equal(NoteTypes.Reference, note.Target["type"]);
        Assert.Equal(artifactId, note.Target["sourceArtifactId"]);
        var draft = s.H.Notes.Unrouted().Single();
        Assert.Contains("Deputy", draft.Text);

        // The artifact is a searchable source from now on.
        Assert.Contains(s.H.Index.Search("7shifts", null, 5), hit => hit.Kind == SearchIndex.ArtifactKind && hit.Id == artifactId);
    }

    [Fact]
    public void RejectingTheRequestSendsNothing()
    {
        var mind = new ScriptedMind();
        var (s, model) = Seeded(_tmp, mind);
        using var _ = s;
        s.Command("research Lightshift's competitors").Reject(Actions.ModelRequest, "not now")
            .ExpectState(RelayState.Ready).ExpectOutcome("rejected")
            .ExpectAnswerContains("Nothing was sent")
            .ExpectNoEvent(EventTypes.ExternalPackaged);
        Assert.Empty(model.Requests);
        Assert.Empty(Directory.EnumerateFiles(s.H.Root.ExternalArtifactsDirectory));
    }

    [Fact]
    public void AFailingExternalModelFailsTheTaskVisiblyAndStoresNoArtifact()
    {
        var mind = new ScriptedMind();
        var (s, _) = Seeded(_tmp, mind, new ScriptedModelClient().Fail("upstream 503"));
        using var __ = s;
        s.Command("research Lightshift's competitors").Approve(Actions.ModelRequest)
            .PumpUntil("the failure", () => s.ForegroundSettled)
            .ExpectState(RelayState.Ready).ExpectOutcome("failed")
            .ExpectNoEvent(EventTypes.ArtifactStored);
        var proposal = Assert.Single(s.Response.Proposals);
        Assert.Equal("failed", proposal.Status);
        Assert.Contains("upstream 503", proposal.Error);
        Assert.False(s.H.Last(EventTypes.ExternalResponded)!.DataBool("ok"));
        Assert.Empty(s.H.Notes.Unrouted());                                        // nothing is kept without an artifact behind it
    }

    [Fact]
    public void WithNoExternalModelConfiguredTheMindIsToldSoAndAnswersWithTheGapNamed()
    {
        var mind = new ScriptedMind();
        using var s = Scenario.New(_tmp, st => Delegating(st, profile: false), mind: mind).WithWorkspace()
            .Project("Lightshift");
        mind.Always(Researching(s.H.Registry.FindActive("lightshift")!.Id, [], search: true));

        s.Command("research Lightshift's competitors and give me an implementation plan")
            .ExpectState(RelayState.Ready).ExpectNoProposals()
            .ExpectAnswerContains("no model here that could settle it");
        var route = s.H.Records().First(r => r.Type == EventTypes.DecisionMade && r.DataString("decision") == Decider.Route);
        Assert.Equal(Decider.Local, route.DataString("outcome"));
        Assert.Contains("no delegate profile is configured", route.DataString("rationale"));
    }

    [Fact]
    public void AProfileWithoutSearchGetsAnOfflinePackageAndTheReasonSaysSo()
    {
        var mind = new ScriptedMind();
        var (s, model) = Seeded(_tmp, mind, search: false);
        using var _ = s;
        s.Command("research Lightshift's competitors").ExpectState(RelayState.Ready /*was AwaitingApproval*/);
        var proposal = Assert.Single(s.Response.Proposals);
        Assert.Equal("false", proposal.Target["allowSearch"]);
        Assert.Contains(proposal.Reasons, r => r.Contains("No online search"));
        s.Approve().PumpUntil("the reply", () => s.H.Count(EventTypes.ArtifactStored) > 0);
        Assert.Contains("Do not use anything beyond the sources above", model.Requests.Single().Messages.Single(m => m.Role == "user").Content);
    }

    [Fact]
    public void AGrantedOnlineSearchPreferenceIsNamedInTheReason()
    {
        var mind = new ScriptedMind();
        var (s, _) = Seeded(_tmp, mind);
        using var __ = s;
        s.Do("allow online search", c => Assert.True(c.UpdatePreference("sources.allowOnlineSearch", "true")))
            .ExpectPreference("sources.allowOnlineSearch", "true")
            .Command("research Lightshift's competitors").ExpectState(RelayState.Ready /*was AwaitingApproval*/);
        Assert.Contains(Assert.Single(s.Response.Proposals).Reasons, r => r.Contains("allowed by your sources preference"));
    }

    [Fact]
    public void AnObservedTaskCanNeverSendAnythingOutside()
    {
        // Work raised from something overheard runs like any other task, except that a package leaving the machine
        // needs the user's own words: the mind may write the request, and policy refuses it on origin alone.
        var client = new ScriptedModelClient().Reply(Answer);
        var mind = new ListeningMind()
            .When("competitors", "research", "Research Lightshift's competitors.", project: "Lightshift", topic: "lightshift")
            .Works(new ScriptedMind().Always(Researching("lightshift", [], search: true)));
        using var s = Scenario.New(_tmp, st => Delegating(st, listening: true), externalClients: _ => client, mind: mind).WithWorkspace()
            .Project("Lightshift")
            .StartListening()
            .Listen("Someone should look at Lightshift's competitors before the beta.")
            .ExpectTask(TaskKind.Research, TaskStatus.Completed, TaskOrigin.Observed);

        var task = s.FindTask(TaskKind.Research, origin: TaskOrigin.Observed)!;
        var proposal = Assert.Single(task.Proposals);
        Assert.Equal("denied", proposal.Status);
        Assert.Contains(proposal.Reasons, r => r.Contains("only be proposed from a direct request"));
        Assert.Empty(client.Requests);
        s.ExpectNoEvent(EventTypes.ExternalPackaged);
    }

    public void Dispose() => _tmp.Dispose();
}
