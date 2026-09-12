using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The README's Lightshift workflow: "research Lightshift's competitors and give me an implementation plan".
/// The planner climbs the source ladder (notes, excerpts, artifacts), states the two-axis knowledge gap,
/// and proposes one bounded external task that binds the exact package. Approval sends exactly that
/// package; the response is stored as a source artifact; a follow-up shows a concise summary with its
/// limits and files the findings as a separate draft note. The external model is scripted; everything
/// else is the production path.
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

    private static void WithResearchProfile(RelaySettings s, bool supportsSearch = true)
        => s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research", SupportsSearch = supportsSearch });

    /// <summary>A Lightshift project with two notes, a scripted research model, and background completions pumped by the test.</summary>
    private static (Scenario S, ScriptedModelClient Model) Seeded(TempRoot tmp, ScriptedModelClient? model = null, bool supportsSearch = true)
    {
        var client = model ?? new ScriptedModelClient().Reply(Answer);
        var s = Scenario.New(tmp, configure: st => WithResearchProfile(st, supportsSearch), externalClients: _ => client, inlinePost: false).WithWorkspace()
            .Command("create project Lightshift").Approve().ExpectProject("lightshift")
            .Note("Lightshift is our scheduling app for shift workers in small restaurants.")
            .Note("We decided Lightshift targets independent restaurants first, chains later.")
            .Command("file all notes under Lightshift").ExpectState(RelayState.Completed);
        return (s, client);
    }

    [Fact]
    public void TheRequestStatesTheKnowledgeGapAndProposesOneBoundPackage()
    {
        var (s, model) = Seeded(_tmp);
        using var _ = s;
        s.Command("research Lightshift's competitors and give me an implementation plan").ExpectState(RelayState.AwaitingApproval);

        var task = s.Response;
        Assert.Equal(TaskKind.Research, task.Kind);
        // Two-axis knowledge state: what is missing, and that the local model cannot do it.
        Assert.Equal(2, task.Knowledge.Known.Count);
        Assert.Contains(task.Knowledge.Missing, m => m.Contains("competitors", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(task.Knowledge.Missing, m => m.Contains("implementation plan", StringComparison.OrdinalIgnoreCase));
        Assert.True(task.Knowledge.CapabilityGap);
        Assert.Contains("Knowledge state", task.Answer);
        Assert.Contains("Missing:", task.Answer);
        Assert.Contains("Capability:", task.Answer);
        Assert.Equal(2, task.Citations.Count);
        // The source ladder is visible in the steps, in order.
        var ladder = task.Steps.Where(st => st.StartsWith("Source ladder", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, ladder.Count);
        Assert.StartsWith("Source ladder 1/3: project notes", ladder[0]);
        Assert.StartsWith("Source ladder 2/3: retained excerpts", ladder[1]);
        Assert.StartsWith("Source ladder 3/3: stored external artifacts", ladder[2]);

        // One proposal binding the exact package.
        var proposal = Assert.Single(task.Proposals);
        Assert.Equal(Actions.ModelRequest, proposal.Action);
        Assert.Equal("pending", proposal.Status);
        Assert.Equal("research", proposal.Target["profile"]);
        Assert.Equal("true", proposal.Target["allowSearch"]);
        Assert.Equal("4000", proposal.Target["budgetTokens"]);
        var refs = proposal.Target["refs"].Split(',');
        Assert.Equal(2, refs.Length);
        Assert.All(refs, r => Assert.Contains(r, task.Knowledge.Known));
        Assert.Contains("References leaving the machine:", proposal.Detail);
        Assert.Contains("online search: true", proposal.Detail);
        Assert.Contains(proposal.Reasons, r => r.Contains("not granted by preference") && r.Contains("this task only"));
        Assert.Empty(model.Requests);                                              // nothing has left the machine
        s.ExpectNoEvent(EventTypes.ExternalPackaged);
    }

    [Fact]
    public void ApprovingSendsExactlyThePackageStoresTheArtifactAndSummarisesWithLimits()
    {
        var (s, model) = Seeded(_tmp);
        using var _ = s;
        s.Command("research Lightshift's competitors and give me an implementation plan").Approve(Actions.ModelRequest)
            .ExpectEvent(EventTypes.ExternalPackaged)                                // recorded before anything is sent
            .PumpUntil("the external response is stored", () => s.H.Count(EventTypes.ArtifactStored) > 0)
            .ExpectState(RelayState.Completed).ExpectOutcome("executed");

        // Exactly the package: the objective plus the two cited notes, nothing else.
        var request = Assert.Single(model.Requests);
        var user = request.Messages.Single(m => m.Role == "user").Content;
        Assert.Contains("Research Lightshift's competitors and produce implementation plan", user);
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

        // The follow-up: a concise summary, the limits named, the artifact cited, and the findings as a separate draft note.
        var summary = s.FindTask(TaskKind.Research, TaskStatus.Completed, TaskOrigin.Dialogue) ?? throw s.Fail("no follow-up task");
        Assert.Equal(s.Response.TaskId, summary.ParentTaskId);
        Assert.NotNull(summary.Answer);
        Assert.True(summary.Answer!.Length < Answer.Length, "the summary must be shorter than the response");
        Assert.Contains("Deputy, When I Work, and 7shifts", summary.Answer);
        Assert.Contains("Limits: I could not determine current pricing", summary.Answer);
        Assert.DoesNotContain("Limits: the response did not name", summary.Answer);
        Assert.Contains(artifactId, summary.Answer);
        var citation = Assert.Single(summary.Citations);
        Assert.Equal(SearchIndex.ArtifactKind, citation.Kind);
        Assert.Equal(artifactId, citation.Id);
        Assert.Equal([artifactId], summary.Knowledge.Known);
        var note = Assert.Single(summary.Proposals);
        Assert.Equal(Actions.CreateDraftNote, note.Action);
        Assert.Equal("executed", note.Status);                                     // staging write: automatic
        Assert.Equal(NoteTypes.Reference, note.Target["type"]);
        Assert.Equal(artifactId, note.Target["sourceArtifactId"]);
        var draft = s.H.Notes.Unrouted().Single();
        Assert.StartsWith("Findings (", draft.Text);
        Assert.Contains("Deputy", draft.Text);

        // Presented as findings, not an alert: the user asked for it.
        Assert.Equal(Presentation.Findings, summary.Presentation);
        s.ExpectAttention(Presentation.Findings, "External result");

        // The artifact is a searchable source from now on.
        s.Command("what do I know about 7shifts").ExpectAnswerContains("artifact");
    }

    [Fact]
    public void RejectingTheRequestSendsNothing()
    {
        var (s, model) = Seeded(_tmp);
        using var _ = s;
        s.Command("research Lightshift's competitors").Reject(Actions.ModelRequest, "not now")
            .ExpectState(RelayState.Completed).ExpectOutcome("rejected")
            .ExpectNoEvent(EventTypes.ExternalPackaged);
        Assert.Empty(model.Requests);
        Assert.Empty(Directory.EnumerateFiles(s.H.Root.ExternalArtifactsDirectory));
    }

    [Fact]
    public void AFailingExternalModelFailsTheTaskVisiblyAndStoresNoArtifact()
    {
        var (s, _) = Seeded(_tmp, new ScriptedModelClient().Fail("upstream 503"));
        using var __ = s;
        s.Command("research Lightshift's competitors").Approve(Actions.ModelRequest)
            .PumpUntil("the failure is recorded", () => s.H.Count(EventTypes.ExternalResponded) > 0)
            .ExpectState(RelayState.Failed).ExpectOutcome("failed")
            .ExpectNoEvent(EventTypes.ArtifactStored);
        var proposal = Assert.Single(s.Response.Proposals);
        Assert.Equal("failed", proposal.Status);
        Assert.Contains("upstream 503", proposal.Error);
        Assert.False(s.H.Last(EventTypes.ExternalResponded)!.DataBool("ok"));
        Assert.Null(s.FindTask(TaskKind.Research, origin: TaskOrigin.Dialogue));   // no follow-up without an artifact
    }

    [Fact]
    public void WithoutAnExternalProfileTheGapIsStatedAndNothingIsProposed()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Lightshift").Approve()
            .Command("research Lightshift's competitors and give me an implementation plan")
            .ExpectState(RelayState.Completed).ExpectNoProposals()
            .ExpectAnswerContains("Knowledge state").ExpectAnswerContains("No external model profile is configured");
        Assert.True(s.Response.Knowledge.CapabilityGap);
        Assert.Empty(s.Response.Knowledge.Known);
    }

    [Fact]
    public void AProfileWithoutSearchGetsAnOfflinePackageAndTheReasonSaysSo()
    {
        var (s, model) = Seeded(_tmp, supportsSearch: false);
        using var _ = s;
        s.Command("research Lightshift's competitors").ExpectState(RelayState.AwaitingApproval);
        var proposal = Assert.Single(s.Response.Proposals);
        Assert.Equal("false", proposal.Target["allowSearch"]);
        Assert.Contains(proposal.Reasons, r => r.Contains("No online search"));
        s.Approve().PumpUntil("the response", () => s.H.Count(EventTypes.ArtifactStored) > 0);
        Assert.Contains("Do not use anything beyond the sources above", model.Requests.Single().Messages.Single(m => m.Role == "user").Content);
    }

    [Fact]
    public void AGrantedOnlineSearchPreferenceIsNamedInTheReason()
    {
        var (s, _) = Seeded(_tmp);
        using var __ = s;
        s.Command("allow online search").ExpectProposal(Actions.UpdatePreference, "pending").Approve()
            .ExpectState(RelayState.Completed).ExpectPreference("sources.allowOnlineSearch", "true");
        s.Command("research Lightshift's competitors").ExpectState(RelayState.AwaitingApproval);
        Assert.Contains(Assert.Single(s.Response.Proposals).Reasons, r => r.Contains("allowed by your sources preference"));
    }

    [Fact]
    public void AskingANamedProfileDirectlyIsAlsoABoundPackage()
    {
        var (s, model) = Seeded(_tmp);
        using var _ = s;
        s.Command("ask research to compare Lightshift with Deputy on pricing").ExpectState(RelayState.AwaitingApproval);
        var proposal = Assert.Single(s.Response.Proposals);
        Assert.Equal(Actions.ModelRequest, proposal.Action);
        Assert.Equal("research", proposal.Target["profile"]);
        Assert.StartsWith("Research compare Lightshift with Deputy on pricing", proposal.Target["objective"]);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public void AnObservedTaskCanNeverSendAnythingOutside()
    {
        // The mind raises a research task from something overheard; a planner that proposes model.request is denied by policy.
        var mind = new ListeningMind().When("competitors", "research", "Research Lightshift's competitors.", project: "Lightshift", topic: "lightshift");
        var planner = new CannedOrchestrator().Otherwise((request, context) =>
        {
            if (request.Origin != TaskOrigin.Observed) return TurnPlan.NotUnderstood("canned", "only observed tasks are scripted");
            var p = new Proposal(Relay.Core.Ids.Ulid.NewUlid(request.At), Actions.ModelRequest, "Overheard a research need.",
                new Dictionary<string, string> { ["profile"] = "research", ["objective"] = "Research Lightshift's competitors.", ["refs"] = "", ["budgetTokens"] = "1000", ["allowSearch"] = "true" },
                [request.SourceEventId], ["Sends a package"], Risks.ControlledWrite, true, Producers.Model);
            return new TurnPlan(true, "Research overheard", ["Proposed an external task"], null, [], [p], "canned", Knowledge: new KnowledgeState([], ["competitors"], true, "gap"));
        });
        var client = new ScriptedModelClient().Reply(Answer);
        using var s = Scenario.New(_tmp, configure: st => { WithResearchProfile(st); st.Orchestrator.Mode = Relay.Core.Config.OrchestratorSettings.Rules; st.Listening.Enabled = true; },
                orchestrator: new CompositeOrchestrator(new RuleBasedOrchestrator(), planner), mind: mind, externalClients: _ => client, inlinePost: false).WithWorkspace()
            .Command("create project Lightshift").Approve()
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
