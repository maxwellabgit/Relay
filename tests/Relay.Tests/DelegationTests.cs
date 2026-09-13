using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.External;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Policy;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// Delegation in mind mode (docs/09 slice 5): the mind writes the prompt, the package needs one approval, the
/// reply streams back and is digested into feed lines by the local model, the conversation may continue for a
/// bounded number of turns under that one approval, and a refusal carries the user's words back to the mind so
/// delegation stays an offer with a retry-locally option.
/// </summary>
public class DelegationTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DelegationTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static void MindMode(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    private static void WithResearchProfile(RelaySettings s)
        => s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research" });

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static string LongReply(string head) => head + " " + string.Join(" ", Enumerable.Range(1, 90).Select(i => $"point{i}"));

    [Fact]
    public void AConversationContinuesUnderOneApprovalForItsBoundedTurnsAndThenNeedsANewRequest()
    {
        var external = new ScriptedDrafter()
            .Reply("Turn one: UK councils license pilots under local procurement rules.")
            .Reply("Turn two: the constraint is a 12-week cap on unlicensed pilots.")
            .Reply("Turn three: yes, Lightshift would need a data-processing agreement.");
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Summarize how UK councils license scheduling software pilots for restaurants."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 1 } d => MindStep.Of(Reply(d.RequestId, "What is the main constraint on such pilots?"), "Asking a follow-up"),
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 2 } d => MindStep.Of(Reply(d.RequestId, "Would Lightshift need a data-processing agreement?"), "Asking one more"),
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 3 } d => MindStep.Of(Reply(d.RequestId, "And one more thing?"), "Trying a fourth turn"),
                SystemObserved s when s.Text.Contains("used its 3 turns", StringComparison.Ordinal) => MindStep.Of(Say("Research settled it in three turns: a 12-week cap and a data-processing agreement."), "Answered"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research UK council licensing for scheduling pilots")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.ModelRequest, "pending");
        Assert.Equal(0, external.Calls);
        s.Approve(Actions.ModelRequest)
            .PumpUntil("three turns and the answer", () => s.ForegroundSettled, Wait);
        _output.WriteLine(s.Transcript());
        _output.WriteLine("--- the mind's last transcript ---");
        foreach (var o in mind.Requests[^1].Transcript) _output.WriteLine(o.Render());
        s.ExpectState(RelayState.Ready)
            .ExpectAnswerContains("three turns")
            .ExpectEvent(EventTypes.DelegateTurnRefused);

        // One approval, three exchanges: the follow-ups ran as allowed proposals covered by the first.
        Assert.Equal(3, external.Calls);
        var requests = s.Response.Proposals.Where(p => p.Action == Actions.ModelRequest).ToList();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, p => Assert.Equal("executed", p.Status));
        Assert.Equal(1, s.H.Records().Count(r => r.Type == EventTypes.ApprovalGranted));
        Assert.Contains(requests[1].Reasons, r => r.StartsWith("Covered by an earlier approval", StringComparison.Ordinal));
        Assert.StartsWith("Continue the conversation with external model 'research' (turn 2)", requests[1].Title);

        // The delegate saw the whole conversation on turn two: the system prompt, the first package, its own first answer, the follow-up.
        var second = external.Requests[1];
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, second.Messages.Select(m => m.Role));
        Assert.Contains("Turn one", second.Messages[2].Content);
        Assert.Equal("What is the main constraint on such pilots?", second.Messages[3].Content);
        Assert.Equal(6, external.Requests[2].Messages.Count);

        // Every turn is its own package and artifact, chained by the conversation id; the ledger records the turn on each.
        var conversationId = requests[0].ProposalId;
        var packaged = s.H.Records().Where(r => r.Type == EventTypes.ExternalPackaged).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, packaged.Select(r => (int)r.DataInt64("turn")!));
        Assert.All(packaged, r => Assert.Equal(conversationId, r.DataString("conversationId")));
        Assert.Equal(0, packaged[1].DataInt64("sources"));
        var artifacts = s.H.External!.AllArtifacts().Select(a => s.H.External.ReadArtifactRecord(a.Id)!).OrderBy(a => a.Turn).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, artifacts.Select(a => a.Turn));
        Assert.All(artifacts, a => Assert.Equal(conversationId, a.ConversationId));

        // The mind saw the turns count down and the refusal named the bound and the way forward.
        var returned = mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>().Where(d => d.Stage == DelegateObserved.Returned).DistinctBy(d => d.RequestId).ToList();
        Assert.Equal(new[] { (1, 2), (2, 1), (3, 0) }, returned.Select(d => (d.Turn, d.TurnsLeft)));
        Assert.Contains("reply_to=" + conversationId, returned[0].Render());
        Assert.Contains("used its turns", returned[2].Render());
        var refused = mind.Requests.SelectMany(r => r.Transcript).OfType<SystemObserved>().Single(o => o.Text.Contains("used its 3 turns", StringComparison.Ordinal));
        Assert.Contains("To delegate afresh, use delegate without reply_to", refused.Text);
        // The reply's own first line is the digest when no local model digests it.
        Assert.Contains(s.Response.Steps, step => step == "· Turn one: UK councils license pilots under local procurement rules.");
    }

    [Fact]
    public void AFollowUpWithNewLocalSourcesIsRefusedAndAFreshRequestNeedsTheUser()
    {
        var external = new ScriptedDrafter().Reply("First answer.").Reply("Second answer, with the note.");
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Question one."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 1 } d => MindStep.Of(Reply(d.RequestId, "Now consider this note too.", "01ARZ3NDEKTSV4RRFFQ69G5FAV"), "Adding a note to the conversation"),
                SystemObserved s when s.Text.Contains("may not add local sources", StringComparison.Ordinal) => MindStep.Of(Delegate("research", "Question two, with the note."), "Asking afresh"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research something")
            .Approve(Actions.ModelRequest);
        try { s.PumpUntil("the second request to wait for approval", () => s.AwaitingUserOrSettled, Wait); }
        finally
        {
            _output.WriteLine(s.Transcript());
            foreach (var o in mind.Requests[^1].Transcript) _output.WriteLine(o.Render());
        }
        s.ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectEvent(EventTypes.DelegateTurnRefused);

        // The refusal did not leave the machine; the fresh request is a new card, not a turn.
        Assert.Equal(1, external.Calls);
        var pending = Assert.Single(s.Snap.PendingProposals);
        Assert.Equal(Actions.ModelRequest, pending.Action);
        Assert.StartsWith("Send a package to external model", pending.Title);
        var refused = s.H.Last(EventTypes.DelegateTurnRefused)!;
        Assert.Contains("01ARZ3NDEKTSV4RRFFQ69G5FAV", refused.DataString("reason"));
    }

    [Fact]
    public void AReplyIsDigestedIntoThreeFeedLinesByTheLocalModelAndTheMindReadsThemFirst()
    {
        var external = new ScriptedDrafter().Reply(LongReply("The licensing regime rests on three instruments."));
        var digester = new ScriptedDrafter().Reply(new { lines = new[] { "Councils license pilots under three instruments.", "The binding one is the 12-week cap.", "Fees were not in the sources." } });
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Explain the licensing regime."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned } d => MindStep.Of(Say("In short: " + string.Join(" ", d.Digest ?? [])), "Answered from the digest"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind, digester: digester)
            .WithSecret("external-research")
            .Ask("explain the licensing regime")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("the digested reply", () => s.ForegroundSettled, Wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("12-week cap")
            .ExpectEvent(EventTypes.DelegateDigested);
        _output.WriteLine(s.Transcript());

        // The digester saw the objective and the reply under the digest schema; the feed carries its three lines under the mind's sentence.
        var request = Assert.Single(digester.Requests);
        Assert.Equal(Digest.SchemaName, request.SchemaName);
        Assert.Contains("Explain the licensing regime.", request.Messages[1].Content);
        Assert.Contains("three instruments", request.Messages[1].Content);
        Assert.Equal(new[] { "Asking research", "Research is working", "· Councils license pilots under three instruments.", "· The binding one is the 12-week cap.", "· Fees were not in the sources.", "Answered from the digest" }, s.Response.Steps);
        var digested = s.H.Last(EventTypes.DelegateDigested)!;
        Assert.Equal("drafter:scripted", digested.DataString("digestBy"));
        Assert.Equal(1, digested.DataInt64("turn"));
        // The artifact keeps the digest beside the whole reply.
        var artifact = s.H.External!.AllArtifacts().Select(a => s.H.External.ReadArtifactRecord(a.Id)!).Single();
        Assert.Equal(3, artifact.Digest!.Count);
        Assert.Equal("drafter:scripted", artifact.DigestBy);
        Assert.StartsWith("The licensing regime", artifact.Text);
        var returned = mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>().Single(d => d.Stage == DelegateObserved.Returned);
        Assert.Contains("digest: Councils license pilots under three instruments. / The binding one is the 12-week cap.", returned.Render());
    }

    [Fact]
    public void ShortRepliesAndAFailedDigesterFallBackToTheReplysOwnLines()
    {
        // Short: the model is not asked at all. Long with a digester that fails: the reply's first lines, and the failure is in the ledger.
        var external = new ScriptedDrafter().Reply("Short answer.\nSecond line.").Reply(LongReply("Long answer first line."));
        var digester = new ScriptedDrafter(); // no replies scripted: the first call fails
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Question one."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 1 } d => MindStep.Of(Reply(d.RequestId, "Question two."), "Asking again"),
                DelegateObserved { Stage: DelegateObserved.Returned, Turn: 2 } => MindStep.Of(Say("Both answered."), "Answered"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind, digester: digester)
            .WithSecret("external-research")
            .Ask("two questions")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("both turns", () => s.ForegroundSettled, Wait).ExpectState(RelayState.Ready);
        _output.WriteLine(s.Transcript());

        Assert.Equal(1, digester.Calls); // only the long reply went to the digester
        var digests = s.H.Records().Where(r => r.Type == EventTypes.DelegateDigested).ToList();
        Assert.Equal(2, digests.Count);
        Assert.Null(digests[0].DataString("digestBy"));
        Assert.Null(digests[0].DataString("digestError"));
        Assert.Null(digests[1].DataString("digestBy"));
        Assert.Contains("no reply left", digests[1].DataString("digestError"));
        Assert.Contains(s.Response.Steps, step => step == "· Short answer.");
        Assert.Contains(s.Response.Steps, step => step == "· Second line.");
        Assert.Contains(s.Response.Steps, step => step.StartsWith("· Long answer first line.", StringComparison.Ordinal));
    }

    [Fact]
    public void ARejectionCarriesTheUsersWordsToTheMindSoDelegationStaysAnOffer()
    {
        var external = new ScriptedDrafter().Reply("never sent");
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Research the licensing rules."), "Offering to ask research", Read(0.8, MindRead.NeedExternalReasoning))
            .Then(req =>
            {
                var approval = Assert.IsType<ApprovalObserved>(req.Transcript[^1]);
                Assert.False(approval.Granted);
                Assert.Equal("retry locally: try again without an external model", approval.Reason);
                return MindStep.Of(Say("Understood; here is what the notes say instead."), "Answering locally");
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research the licensing rules")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .Reject(Actions.ModelRequest, "retry locally: try again without an external model")
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("notes say instead");
        _output.WriteLine(s.Transcript());
        Assert.Equal(0, external.Calls);
        Assert.Contains("retry locally", mind.Requests[1].Transcript.OfType<ApprovalObserved>().Single().Render());
        // A bare rejection carries no words: the mind is told only that it was rejected.
        Assert.Equal("retry locally: try again without an external model", s.H.Last(EventTypes.ApprovalRejected)!.DataString("reason"));
    }

    [Fact]
    public void AFailedDelegateClosesItsConversationAndTheMindIsToldItMayAskAgain()
    {
        var external = new ScriptedDrafter(); // no replies: the request fails
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Question."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Failed } d => MindStep.Of(Reply(d.RequestId, "Are you there?"), "Trying to continue"),
                SystemObserved s when s.Text.Contains("not an open conversation", StringComparison.Ordinal) => MindStep.Of(Say("Research was unavailable; I answered from the notes."), "Answered locally"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research something")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("the failure and the answer", () => s.ForegroundSettled, Wait);
        _output.WriteLine(s.Transcript());
        var failed = mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>().First(d => d.Stage == DelegateObserved.Failed);
        Assert.Contains("You may delegate again (the user will be asked)", failed.Text);
        Assert.Contains(s.Response.Steps, step => step == "Answered locally");
        Assert.Equal(1, s.H.Records().Count(r => r.Type == EventTypes.DelegateTurnRefused));
    }

    [Fact]
    public void ARequestForSearchFromAProfileWithoutItGoesWithoutSearchAndTheMindIsTold()
    {
        // Seen live: the mind asked for search, the user approved the card, and the runtime then failed the request. Capability is configuration, known before the card.
        var external = new ScriptedDrafter().Reply("Answer from the package alone.");
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Look this up.", search: true), "Asking research to search", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned } d => MindStep.Of(Say("Done: " + d.Text), "Answered"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("look this up")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/);
        var card = Assert.Single(s.Snap.PendingProposals);
        Assert.Contains(card.Reasons, r => r.StartsWith("No online search", StringComparison.Ordinal));
        s.Approve(Actions.ModelRequest)
            .PumpUntil("the reply", () => s.ForegroundSettled, Wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("from the package alone");
        _output.WriteLine(s.Transcript());
        // The correction reached the mind before the card's decision did, and the request went out without search.
        var transcript = mind.Requests[^1].Transcript;
        var told = Assert.Single(transcript.OfType<SystemObserved>().Where(o => o.Text.Contains("cannot search online", StringComparison.Ordinal)));
        Assert.Contains("No configured profile can.", told.Text);
        Assert.True(transcript.ToList().IndexOf(told) < transcript.ToList().FindIndex(o => o is PolicyObserved));
        Assert.Equal("false", s.Response.Proposals.Single(p => p.Action == Actions.ModelRequest).Target["allowSearch"]);
        Assert.Equal(1, external.Calls);
    }

    [Fact]
    public void RefsRelayDoesNotHoldAreLeftOutOfThePackageAndTheMindIsToldWhichWereReal()
    {
        // Seen live: the mind wrote refs "none" and policy denied the package; a made-up id would do the same. Placeholders are dropped at parsing,
        // unknown ids are dropped at the request with the mind told, and the package goes with what is real.
        var parsed = Assert.IsType<DelegateMove>(MoveSchema.Parse("""{"read":{"intent":"x","complexity":0.8,"needs":["external_reasoning"],"significance":0.5,"sensitivity":0,"risk":{"core":0,"security":0,"loop":0,"destructive":0}},"move":{"type":"delegate","name":"research","text":"Look this up.","args":{"refs":"none, <ids returned by tools>, N/A"}},"feed":"x"}""").Move);
        Assert.Empty(parsed.Refs);

        var external = new ScriptedDrafter().Reply("Answer.");
        string? noteId = null;
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateDraftNote, "keep the question", ("text", "Lightshift pilot licensing question"), ("type", "question")), "Keeping the question", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                ExecutionObserved { Ok: true } e when noteId is null => MindStep.Of(Delegate("research", "Look this up.", 2_000, false, noteId = e.Outputs["noteId"], "01ARZ3NDEKTSV4RRFFQ69G5FAV"), "Asking research"),
                DelegateObserved { Stage: DelegateObserved.Returned } d => MindStep.Of(Say("Done: " + d.Text), "Answered"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("look this up")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/);
        var card = Assert.Single(s.Snap.PendingProposals);
        Assert.Equal(noteId, card.Target["refs"]);
        s.Approve(Actions.ModelRequest)
            .PumpUntil("the reply", () => s.ForegroundSettled, Wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("Done: Answer.");
        _output.WriteLine(s.Transcript());
        var told = Assert.Single(mind.Requests[^1].Transcript.OfType<SystemObserved>().Where(o => o.Text.StartsWith("Left out of the package: 01ARZ3NDEKTSV4RRFFQ69G5FAV", StringComparison.Ordinal)));
        Assert.Contains("it carries " + noteId, told.Text);
        Assert.Equal(1, s.H.Last(EventTypes.ExternalPackaged)!.DataInt64("sources"));
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.ProposalDecided && r.DataString("outcome") == "Deny");
    }

    [Fact]
    public void ASecondRequestWhileOneIsAnsweringIsRefusedAndTheReplyIsReadInstead()
    {
        // Seen live: in the step after "started" the mind sent a second request, and the user was asked to approve it while the reply was arriving.
        var external = new ScriptedDrafter().Reply("The one reply.");
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Question."), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Started } => MindStep.Of(Delegate("research", "Question, again."), "Asking again"),
                DelegateObserved { Stage: DelegateObserved.Returned } d => MindStep.Of(Say("Answer: " + d.Text), "Answered"),
                SystemObserved s when s.Text.Contains("still answering", StringComparison.Ordinal) => MindStep.Of(Wait("waiting for the reply"), "Waiting for research"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("one question")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("the reply and the answer", () => s.ForegroundSettled, Wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("The one reply.");
        _output.WriteLine(s.Transcript());
        Assert.Equal(1, external.Calls);
        Assert.Single(s.Response.Proposals.Where(p => p.Action == Actions.ModelRequest));
        Assert.Equal(1, s.H.Records().Count(r => r.Type == EventTypes.ProposalReceived));
        var started = Assert.Single(mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>().Where(d => d.Stage == DelegateObserved.Started).DistinctBy(d => d.RequestId));
        Assert.Contains("wait for it (move: wait)", started.Render());
        // The refusal reached the mind; whether it got a step of its own before the reply depends on how fast the reply came.
        Assert.Contains(mind.Requests[^1].Transcript.OfType<SystemObserved>(), o => o.Text.Contains("is still answering; no second request is sent", StringComparison.Ordinal));
        Assert.Equal(new[] { "Asking research", "Asking again" }, s.Response.Steps.Take(2));
        Assert.Equal(new[] { "· The one reply.", "Answered" }, s.Response.Steps.TakeLast(2));
    }

    [Fact]
    public void TheSamePromptAgainAfterAReplyIsRefusedWithTheReplyPointedAt()
    {
        // Seen live: with the reply and its digest in front of it, the mind re-sent the very ask it had just had answered, as a fresh request.
        var external = new ScriptedDrafter().Reply(LongReply("The answer is the 12-week cap."));
        var mind = new ScriptedMind()
            .Step(Delegate("research", "What is the cap?"), "Asking research", Read(0.8, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned } => MindStep.Of(Delegate("research", "what is the cap? "), "Asking research again"),
                SystemObserved s when s.Text.StartsWith("You already delegated exactly this prompt", StringComparison.Ordinal) => MindStep.Of(Say("The cap is 12 weeks."), "Answered from the reply"),
                DelegateObserved => MindStep.Of(Wait("working"), "Research is working"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("what is the cap")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("the answer", () => s.ForegroundSettled, Wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("12 weeks");
        _output.WriteLine(s.Transcript());
        Assert.Equal(1, external.Calls);
        Assert.Single(s.Response.Proposals.Where(p => p.Action == Actions.ModelRequest));
        var refused = mind.Requests[^1].Transcript.OfType<SystemObserved>().Single(o => o.Text.StartsWith("You already delegated exactly this prompt", StringComparison.Ordinal));
        Assert.Contains("returned: The answer is the 12-week cap.", refused.Text);
        Assert.Contains("reply_to=", refused.Text);
    }

    [Fact]
    public void ADelegateMoveCanNameTheTurnItContinuesAndNeedsNoProfileThen()
    {
        var step = MoveSchema.Parse("""{"read":{"intent":"follow up","complexity":0.5,"needs":["external_reasoning"],"significance":0.5,"sensitivity":0,"risk":{"core":0,"security":0,"loop":0,"destructive":0}},"move":{"type":"delegate","text":"What about fees?","args":{"reply_to":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}},"feed":"Asking about fees"}""");
        var move = Assert.IsType<DelegateMove>(step.Move);
        Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", move.ReplyTo);
        Assert.Equal("", move.Profile);
        Assert.Contains("reply to 01ARZ3NDEKTSV4RRFFQ69G5FAV", move.Brief());
        Assert.Throws<FormatException>(() => MoveSchema.Parse("""{"read":{"intent":"x","complexity":0.5,"needs":["external_reasoning"],"significance":0.5,"sensitivity":0,"risk":{"core":0,"security":0,"loop":0,"destructive":0}},"move":{"type":"delegate","text":"no profile, no reply_to"},"feed":"x"}"""));
    }

    [Fact]
    public void TheDigestParserTakesLinesAnArrayOrJoinedTextAndPlainTakesFirstLines()
    {
        Assert.Equal(new[] { "a", "b" }, Digest.Parse("""{"lines":["a","b"]}"""));
        Assert.Equal(new[] { "a", "b" }, Digest.Parse("""["a","b"]"""));
        Assert.Equal(new[] { "a", "b", "c" }, Digest.Parse("""{"lines":["a\nb\nc\nd"]}"""));
        Assert.Empty(Digest.Parse("not json"));
        Assert.Empty(Digest.Parse("""{"other":1}"""));
        var plain = Digest.Plain("# Heading\n\n- first point\nsecond point\nthird\nfourth");
        Assert.Equal(new[] { "Heading", "first point", "second point" }, plain);
        Assert.Equal(Digest.MaxLineChars, Digest.Plain(new string('x', 500))[0].Length);
        Assert.Contains("Reply with one JSON object", Digest.User("objective", "reply"));
    }

    /// <summary>
    /// The slice 5 acceptance with a real model in two seats: the mind writes the delegate prompt, the user approves the one request,
    /// a scripted counterparty answers at length, the same model digests the reply into feed lines, and the mind answers from what
    /// came back — continuing the conversation with reply_to under the same approval if it wants to. Runs only with
    /// RELAY_LIVE_MODEL_KEY; the transcript and the mind's last transcript go to RELAY_LIVE_REPORT_DIR.
    /// </summary>
    [Fact]
    public void LiveModelMindDelegatesReadsTheDigestAndAnswers()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        var wait = TimeSpan.FromSeconds(300);
        var external = new ScriptedDrafter()
            .Reply(LiveCounterparty.FirstReply)
            .Reply(LiveCounterparty.SecondReply)
            .Reply(LiveCounterparty.ThirdReply);
        var mind = new RecordingMind(new ModelMind(client));
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); MindMode(cfg); WithResearchProfile(cfg); cfg.Orchestrator.MaxSteps = 10; },
            externalClients: _ => external, inlinePost: false, mind: mind, digester: client).WithSecret("external-research");
        try
        {
            s.Ask("Research how UK councils license scheduling-software pilots for restaurants, and what Lightshift would need for one.")
             .PumpUntil("the mind to write the delegate request", () => s.AwaitingUserOrSettled, wait);
            _output.WriteLine(s.Transcript());

            // The mind delegated rather than guessed: one package waits for the user, nothing has left the machine.
            Assert.Equal(RelayState.Ready /*was AwaitingApproval*/, s.Snap.State);
            var card = Assert.Single(s.Snap.PendingProposals);
            Assert.Equal(Actions.ModelRequest, card.Action);
            Assert.Equal(0, external.Calls);

            // The user approves each fresh request the mind writes (a follow-up under reply_to needs none); the counterparty has three replies.
            var approvals = 0;
            while (true)
            {
                s.Approve(Actions.ModelRequest);
                approvals++;
                s.PumpUntil("the reply, its digest and the answer", () => s.AwaitingUserOrSettled, wait);
                if (s.Snap.State != RelayState.Ready /*was AwaitingApproval*/ || approvals >= ExternalRuntime.DefaultMaxTurns) break;
                if (Assert.Single(s.Snap.PendingProposals).Action != Actions.ModelRequest) break;
            }
            s.ExpectState(RelayState.Ready).ExpectEvent(EventTypes.DelegateDigested);

            // One approval per fresh request, none per follow-up turn; every reply was digested by the model into one to three bounded lines.
            Assert.Equal(approvals, s.H.Records().Count(r => r.Type == EventTypes.ApprovalGranted));
            Assert.InRange(external.Calls, 1, ExternalRuntime.DefaultMaxTurns);
            var digests = s.H.Records().Where(r => r.Type == EventTypes.DelegateDigested).ToList();
            Assert.Equal(external.Calls, digests.Count);
            foreach (var artifact in s.H.External!.AllArtifacts().Select(a => s.H.External.ReadArtifactRecord(a.Id)!))
            {
                Assert.NotNull(artifact.Digest);
                Assert.InRange(artifact.Digest!.Count, 1, Digest.MaxLines);
                Assert.All(artifact.Digest, line => Assert.InRange(line.Length, 1, Digest.MaxLineChars));
                Assert.Equal(live.Model, artifact.DigestBy);
            }
            // The answer repeats what came back, not what the mind knew: the counterparty's particulars are in it.
            var answer = s.Response.Answer ?? "";
            Assert.True(LiveCounterparty.Particulars.Any(p => answer.Contains(p, StringComparison.OrdinalIgnoreCase)), "answer: " + answer);
        }
        finally
        {
            _output.WriteLine(s.Transcript());
            var sb = new System.Text.StringBuilder(s.Transcript());
            if (mind.Requests.Count > 0)
            {
                sb.AppendLine().AppendLine("--- the mind's last transcript ---");
                foreach (var o in mind.Requests[^1].Transcript) sb.AppendLine(o.Render());
            }
            sb.AppendLine().AppendLine("--- what the delegate was sent ---");
            foreach (var (request, i) in external.Requests.Select((r, i) => (r, i)))
            {
                sb.AppendLine($"turn {i + 1}:");
                foreach (var m in request.Messages) sb.AppendLine($"  [{m.Role}] {m.Content}");
            }
            live.Write("mind-delegate-live.txt", sb.ToString());
            live.Write("mind-delegate-live.ledger.jsonl", s.H.LedgerText());
        }
    }

    /// <summary>Keeps every request the live mind was given, so the report can show what it read before each step.</summary>
    private sealed class RecordingMind(IMind inner) : IMind
    {
        public List<MindRequest> Requests { get; } = [];
        public string Name => inner.Name;
        public Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return inner.StepAsync(request, cancellationToken);
        }
    }

    /// <summary>The scripted counterparty of the live test: long enough to need digesting, with particulars the mind cannot know on its own.</summary>
    private static class LiveCounterparty
    {
        public static readonly string[] Particulars = ["Halverson", "14-week", "14 week", "fourteen", "Schedule 9", "Bramley", "£340", "340"];

        public const string FirstReply =
            "UK councils do not license scheduling software as such; what they license is the pilot as a procurement exception. " +
            "Under the 2024 Halverson guidance, a council may run an unlicensed pilot with a supplier for at most 14 weeks before it must either " +
            "tender or stop, and the pilot must be registered with the council's procurement officer before the first restaurant is onboarded. " +
            "Three instruments matter in practice: the Halverson guidance itself, Schedule 9 of the local procurement code (which caps pilot spend at £340 " +
            "per participating business), and the council's own data-processing standard, which every supplier signs before any staff rota leaves a restaurant. " +
            "The Bramley Borough pilot of 2025 is the usual reference case: a scheduling supplier onboarded eleven restaurants, was registered on day one, " +
            "and converted to a tender at week 12. Fees to the supplier were not disclosed in the sources. What I could not determine: whether the 14-week cap " +
            "counts from registration or from the first onboarding, and whether Lightshift's staff-rota data would count as personal data under the standard " +
            "(most councils treat named shift patterns as personal data, so the safe assumption is yes).";

        public const string SecondReply =
            "For Lightshift specifically: register the pilot with the council's procurement officer first, sign the council's data-processing standard, " +
            "keep spend under £340 per restaurant so Schedule 9 applies, and plan the tender decision for week 12 as Bramley did. Named shift patterns " +
            "should be treated as personal data. The 14-week cap is best read as counting from registration; the sources do not settle it.";

        public const string ThirdReply =
            "Nothing further in the sources. The Halverson guidance, Schedule 9 and the council's data-processing standard are the three documents to read; " +
            "the Bramley pilot report is the worked example.";
    }

    public void Dispose() => _tmp.Dispose();
}
