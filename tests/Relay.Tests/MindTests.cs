using Relay.Core.Decisions;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>The contract: what the mind may answer with and how it is read.</summary>
public class MoveSchemaTests
{
    private const string FullReply = """
        {"read":{"intent":"find notes about Kathmandu","complexity":0.2,"needs":["local_notes"],"significance":0.1,"sensitivity":0,
          "risk":{"core":0,"security":0,"loop":0,"destructive":0}},
         "move":{"type":"use_tool","text":"","name":"search","args":{"query":"Kathmandu","limit":"5"},"done":false},
         "feed":"Checking your notes for Kathmandu."}
        """;

    [Fact]
    public void AFullReplyBecomesATypedStep()
    {
        var step = MoveSchema.Parse(FullReply, promptChars: 1000, elapsedMs: 250, promptTokens: 300, completionTokens: 60);
        Assert.True(step.Ok);
        var tool = Assert.IsType<UseToolMove>(step.Move);
        Assert.Equal("search", tool.Tool);
        Assert.Equal("Kathmandu", tool.Args["query"]);
        Assert.Equal("5", tool.Args["limit"]);
        Assert.Equal("Checking your notes for Kathmandu.", step.Feed);
        Assert.NotNull(step.Read);
        Assert.Equal(0.2, step.Read!.Complexity);
        Assert.True(step.Read.Has(MindRead.NeedLocalNotes));
        Assert.Equal(0, step.Read.Risk.Max);
        Assert.Equal(300, step.PromptTokens);
        Assert.Equal(FullReply, step.Raw);
    }

    [Fact]
    public void EveryMoveTypeParsesFromTheFlatForm()
    {
        Assert.IsType<SayMove>(Move("""{"type":"say","text":"It is 14:30 in Kathmandu.","name":"","args":{},"done":true}"""));
        Assert.True(((SayMove)Move("""{"type":"say","text":"x","name":"","args":{},"done":true}""")).Done);
        var propose = Assert.IsType<ProposeMove>(Move("""{"type":"propose","text":"The user asked for it.","name":"create_project","args":{"name":"Atlas"},"done":false}"""));
        Assert.Equal("Atlas", propose.Target["name"]);
        Assert.Equal("The user asked for it.", propose.Reason);
        var delegated = Assert.IsType<DelegateMove>(Move("""{"type":"delegate","text":"Summarise the attached.","name":"research","args":{"refs":"n1, n2","budget_tokens":"3000","allow_search":"true"},"done":false}"""));
        Assert.Equal(["n1", "n2"], delegated.Refs);
        Assert.Equal(3000, delegated.BudgetTokens);
        Assert.True(delegated.AllowSearch);
        // A tiny budget is raised to the floor; a missing one takes the default.
        Assert.Equal(MoveSchema.MinDelegateBudget, ((DelegateMove)Move("""{"type":"delegate","text":"Summarise.","name":"research","args":{"budget_tokens":"200"},"done":false}""")).BudgetTokens);
        Assert.Equal(MoveSchema.DefaultDelegateBudget, ((DelegateMove)Move("""{"type":"delegate","text":"Summarise.","name":"research","args":{},"done":false}""")).BudgetTokens);
        var build = Assert.IsType<BuildMove>(Move("""{"type":"build","text":"We need a world clock.","name":"World Clock!","args":{"inputs":"city","outputs":"local time"},"done":false}"""));
        Assert.Equal("world_clock", build.Name);
        Assert.Equal("city", build.Inputs);
        var ask = Assert.IsType<AskUserMove>(Move("""{"type":"ask_user","text":"Which project?","name":"","args":{"options":"Atlas|Backyard"},"done":false}"""));
        Assert.Equal(["Atlas", "Backyard"], ask.Options);
        Assert.Equal("nothing here", ((WaitMove)Move("""{"type":"wait","text":"nothing here","name":"","args":{},"done":false}""")).Reason);
        Assert.IsType<StopMove>(Move("""{"type":"stop","text":"enough","name":"","args":{},"done":false}"""));
    }

    [Fact]
    public void WhatMattersIsStrictAndWhatDoesNotIsTolerated()
    {
        Assert.Contains("unknown move type", Assert.Throws<FormatException>(() => Move("""{"type":"fly","text":"","name":"","args":{},"done":false}""")).Message);
        Assert.Contains("needs text", Assert.Throws<FormatException>(() => Move("""{"type":"say","text":"","name":"","args":{},"done":true}""")).Message);
        Assert.Contains("tool's name", Assert.Throws<FormatException>(() => Move("""{"type":"use_tool","text":"","name":"","args":{},"done":false}""")).Message);
        Assert.Contains("prompt", Assert.Throws<FormatException>(() => Move("""{"type":"delegate","text":"","name":"research","args":{},"done":false}""")).Message);
        Assert.Contains("not valid JSON", Assert.Throws<FormatException>(() => MoveSchema.Parse("not json")).Message);
        Assert.Contains("no 'move'", Assert.Throws<FormatException>(() => MoveSchema.Parse("""{"feed":"hello"}""")).Message);

        // A move at the top level, aliases for the field names, numbers as strings, an over-long feed: all read.
        var step = MoveSchema.Parse("{\"type\":\"use_tool\",\"tool\":\"list_projects\",\"args\":{},\"read\":{\"complexity\":\"0.4\",\"needs\":[\"bogus\",\"world_knowledge\"],\"risk\":{\"security\":\"2\"}},\"feed\":\"" + new string('x', 400) + "\"}");
        Assert.Equal("list_projects", ((UseToolMove)step.Move!).Tool);
        Assert.Equal(0.4, step.Read!.Complexity);
        Assert.Equal([MindRead.NeedWorldKnowledge], step.Read.Needs);
        Assert.Equal(1, step.Read.Risk.Security);
        Assert.Equal(MoveSchema.MaxFeedChars, step.Feed.Length);

        // No feed: the move's brief stands in.
        var bare = MoveSchema.Parse("""{"move":{"type":"wait","text":"nothing","name":"","args":{},"done":false},"feed":""}""");
        Assert.Equal("wait \"nothing\"", bare.Feed);
        Assert.Null(bare.Read);
    }

    [Fact]
    public void TheSchemaIsValidJsonAndCoversTheContract()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(MoveSchema.Json)!.AsObject();
        Assert.Equal(["read", "move", "feed"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        var types = schema["properties"]!["move"]!["properties"]!["type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(Relay.Core.Mind.Move.Types.OrderBy(t => t), types.OrderBy(t => t));
        var needs = schema["properties"]!["read"]!["properties"]!["needs"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(MindRead.KnownNeeds.OrderBy(t => t), needs.OrderBy(t => t));
    }

    private static Relay.Core.Mind.Move Move(string moveJson) => MoveSchema.ParseMove(System.Text.Json.Nodes.JsonNode.Parse(moveJson)!.AsObject());
}

public class MindPromptTests
{
    [Fact]
    public void ThePromptTellsTheMindWhatExistsAndWhatDoesNot()
    {
        var bare = MindPrompt.System(new MindContext());
        Assert.Contains("delegate: unavailable", bare);
        Assert.Contains("build: not available", bare);
        Assert.Contains("- search(query, project?, limit?, exclude?)", bare);
        Assert.Contains("- create_project {name, slug?}", bare);
        Assert.Contains("update_prompt", bare);
        Assert.StartsWith(MindPrompt.DefaultConstitution, bare);

        var full = MindPrompt.System(new MindContext { DelegateProfiles = ["research"], SearchProfiles = ["research"], CanBuild = true, ResponseStyle = "Terse.", PromptFragment = "Always answer in English.", Constitution = "You are the test mind.", Actions = ActionCatalog.ForObserved });
        Assert.StartsWith("You are the test mind.", full);
        Assert.Contains("Delegate profiles: research (can search online when approved: research)", full);
        Assert.Contains("- build: name=<snake_case tool name>", full);
        Assert.Contains("Response style (the user's preference; obey it): Terse.", full);
        Assert.Contains("Additional instructions approved by the user: Always answer in English.", full);
        Assert.DoesNotContain("delete_project", full);   // an overheard task may not propose deletion
        Assert.DoesNotContain("update_preference", full);
    }

    [Fact]
    public void TheTranscriptIsNumberedAndRendersEveryObservationKind()
    {
        var at = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var transcript = new List<Observation>
        {
            new InputObserved(at, InputObserved.Ask, "What time is it in Kathmandu?"),
            new MoveObserved(at, new UseToolMove("search", new Dictionary<string, string> { ["query"] = "Kathmandu" }), "Checking notes."),
            new ToolObserved(at, "search", new Dictionary<string, string> { ["query"] = "Kathmandu" }, true, "0 hit(s) for \"Kathmandu\"", "[]", []),
            new PolicyObserved(at, "p1", Actions.ModelRequest, PolicyObserved.Denied, ["no profile 'research' is configured"]),
            new ApprovalObserved(at, "p2", Actions.CreateProject, true),
            new ExecutionObserved(at, "p2", Actions.CreateProject, true, "created", new Dictionary<string, string> { ["projectId"] = "01J" }),
            new DelegateObserved(at, "r1", "research", DelegateObserved.Partial, 1200, "…the capital's offset is +05:45"),
            new BuildObserved(at, "world_clock", BuildObserved.Tested, "3 passed"),
            new UserObserved(at, UserObserved.Reply, "Atlas"),
            new SystemObserved(at, "Route: local."),
        };
        var context = new MindContext { Projects = ["Atlas (id 01J, slug atlas)"], Recall = ["note 01K: Kathmandu trip planned for October"] };
        var text = MindPrompt.Transcript(new MindRequest("t1", InputObserved.Ask, transcript, context, at, 3, MaxSteps: 6));
        Assert.Contains("Task t1 · origin: ask · step 4 of 6", text);
        Assert.DoesNotContain("last step", text);
        Assert.Contains("Projects (the user's active projects; answer from this list without a tool): Atlas (id 01J, slug atlas)", text);
        // The budget is visible: the mind is told when one step remains and when this is the last.
        Assert.Contains("One step remains after this one.", MindPrompt.Transcript(new MindRequest("t1", InputObserved.Ask, transcript, context, at, 4, MaxSteps: 6)));
        Assert.Contains("This is the last step: finish now with say and done=true", MindPrompt.Transcript(new MindRequest("t1", InputObserved.Ask, transcript, context, at, 5, MaxSteps: 6)));
        Assert.DoesNotContain(" of ", MindPrompt.Transcript(new MindRequest("t1", InputObserved.Ask, transcript, context, at, 5)).Split('\n')[1]);   // unknown budget: no count
        Assert.Contains("- note 01K: Kathmandu trip planned for October", text);
        Assert.Contains("[1] ask: \"What time is it in Kathmandu?\"", text);
        Assert.Contains("[2] you → use_tool search {query=Kathmandu} · feed \"Checking notes.\"", text);
        Assert.Contains("[3] tool search {query=Kathmandu} → ok · 0 hit(s) for \"Kathmandu\"", text);
        Assert.Contains("[4] policy: model.request (proposal p1) → denied · no profile 'research' is configured", text);
        Assert.Contains("[5] user approved create_project (proposal p2)", text);
        Assert.Contains("[6] executed create_project (proposal p2) → ok · created · {projectId=01J}", text);
        Assert.Contains("[7] delegate research (request r1) partial · 1200 chars · tail: \"…the capital's offset is +05:45\"", text);
        Assert.Contains("[8] build world_clock → tested · 3 passed", text);
        Assert.Contains("[9] user reply: \"Atlas\"", text);
        Assert.Contains("[10] system: Route: local.", text);
        Assert.EndsWith("Reply with the next step as one JSON object.", text);
    }

    [Fact]
    public async Task TheModelMindSendsTheSchemaAndReportsFailuresAsData()
    {
        var client = new ScriptedModelClient()
            .Reply("""{"read":{"intent":"greet","complexity":0,"needs":["none"],"significance":0,"sensitivity":0,"risk":{"core":0,"security":0,"loop":0,"destructive":0}},"move":{"type":"say","text":"Hello.","name":"","args":{},"done":true},"feed":"Saying hello."}""")
            .Reply("this is not json")
            .Fail("connection refused");
        var mind = new ModelMind(client);
        var request = new MindRequest("t1", InputObserved.Ask, [new InputObserved(DateTimeOffset.UtcNow, InputObserved.Ask, "hello")], new MindContext(), DateTimeOffset.UtcNow, 0);

        var ok = await mind.StepAsync(request, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal("Hello.", ((SayMove)ok.Move!).Text);
        Assert.Equal(MoveSchema.Json, client.Requests[0].JsonSchema);
        Assert.Equal(MoveSchema.SchemaName, client.Requests[0].SchemaName);
        Assert.Equal("system", client.Requests[0].Messages[0].Role);
        Assert.Contains("[1] ask: \"hello\"", client.Requests[0].Messages[1].Content);
        Assert.True(ok.PromptChars > 0);

        var contract = await mind.StepAsync(request, CancellationToken.None);
        Assert.False(contract.Ok);
        Assert.StartsWith("Contract:", contract.Error);
        Assert.Equal("this is not json", contract.Raw);

        var down = await mind.StepAsync(request, CancellationToken.None);
        Assert.False(down.Ok);
        Assert.StartsWith("Model unavailable:", down.Error);
        Assert.Equal("mind:test-model", mind.Name);
    }
}

/// <summary>The self-observing loop: every consequence comes back as an observation; waiting is explicit; budgets end what the mind does not.</summary>
public class TaskLoopTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static (TaskLoop Loop, FakeLoopHost Host, Decider Decider) Build(ScriptedMind mind, MindContext? context = null, LoopBudget? budget = null, string origin = InputObserved.Ask, string input = "What time is it in Kathmandu?")
    {
        var host = new FakeLoopHost();
        var decider = new Decider();
        var loop = new TaskLoop("t1", origin, mind, host, context ?? new MindContext(), decider, budget, new FixedClock(At));
        loop.Observe(new InputObserved(At, origin, input));
        return (loop, host, decider);
    }

    [Fact]
    public async Task AToolResultIsObservedBeforeTheNextStep()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Tool("search", ("query", "Kathmandu")), "Checking your notes.", ScriptedMind.Read(0.2, MindRead.NeedLocalNotes))
            .Then(r =>
            {
                // The second step sees the tool observation that the first move caused.
                Assert.Equal(["input", "move", "tool"], r.Transcript.Select(o => o.Kind));
                Assert.Equal(1, r.StepIndex);
                return MindStep.Of(ScriptedMind.Say("Nothing stored about Kathmandu; it is UTC+05:45, so about 17:45 there."), "Answering from world knowledge.");
            });
        var (loop, host, _) = Build(mind);

        var result = await loop.RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(LoopStatus.Done, result!.Status);
        Assert.Equal("answered", result.Outcome);
        Assert.Contains("17:45", result.Answer);
        Assert.Equal(["Checking your notes.", "Answering from world knowledge."], result.Feed);
        Assert.Equal(2, result.Steps);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(["step:use_tool", "tool:search", "step:say", "said", "ended:answered"], host.Calls);
        Assert.Equal(["input", "move", "tool", "move"], loop.Transcript.Select(o => o.Kind));
        Assert.Equal(Decider.Local, loop.Route!.Outcome);
        Assert.Single(host.Steps.Where(s => s.Move is UseToolMove));
    }

    [Fact]
    public async Task AProposalThatNeedsApprovalPausesTheLoopUntilTheUserAnswersAndTheResultIsObserved()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Propose(Actions.CreateProject, "The user asked for a project.", ("name", "Atlas")), "Proposing a new project Atlas.", ScriptedMind.Read(0.1))
            .Then(r =>
            {
                Assert.Equal(["input", "move", "policy", "approval", "executed"], r.Transcript.Select(o => o.Kind));
                Assert.Equal(PolicyObserved.NeedsApproval, ((PolicyObserved)r.Transcript[2]).Outcome);
                Assert.True(((ApprovalObserved)r.Transcript[3]).Granted);
                Assert.Equal("01J", ((ExecutionObserved)r.Transcript[4]).Outputs["projectId"]);
                return MindStep.Of(ScriptedMind.Say("Atlas is set up."), "Atlas is set up.");
            });
        var (loop, host, _) = Build(mind, input: "create project Atlas");
        host.OnPropose = (m, _) => MoveOutcome.Wait(Waits.Approval, new PolicyObserved(At, "p1", m.Action, PolicyObserved.NeedsApproval, ["Tier B: changes a project"]));

        var first = await loop.RunAsync(CancellationToken.None);
        Assert.Null(first);
        Assert.Equal(LoopStatus.Waiting, loop.Status);
        Assert.Equal(Waits.Approval, loop.WaitingFor);
        Assert.Equal([Waits.Approval], host.Waits);
        Assert.Null(host.LastFof);   // a user-asked project change is not a fundamental operation

        var result = await loop.ResumeAsync(MoveOutcome.Of(
            new ApprovalObserved(At, "p1", Actions.CreateProject, true),
            new ExecutionObserved(At, "p1", Actions.CreateProject, true, "created Atlas", new Dictionary<string, string> { ["projectId"] = "01J" })), CancellationToken.None);

        Assert.Equal(LoopStatus.Done, result!.Status);
        Assert.Equal("Atlas is set up.", result.Answer);
        Assert.Null(loop.WaitingFor);
        Assert.Equal(1, loop.Proposals);
    }

    [Fact]
    public async Task ARejectionAndADenialAreObservedSoTheMindCanTakeAnotherPath()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Propose(Actions.DeleteProject, "asked", ("projectId", "01J"), ("confirm", "delete")), "Proposing to delete Atlas.")
            .Then(r =>
            {
                var approval = Assert.IsType<ApprovalObserved>(r.Transcript[^1]);
                Assert.False(approval.Granted);
                return MindStep.Of(ScriptedMind.Propose(Actions.ArchiveProject, "the user declined deletion", ("projectId", "01J")), "Proposing to archive instead.");
            })
            .Then(r =>
            {
                var policy = Assert.IsType<PolicyObserved>(r.Transcript[^1]);
                Assert.Equal(PolicyObserved.Denied, policy.Outcome);
                Assert.Contains("archived already", policy.Reasons[0]);
                return MindStep.Of(ScriptedMind.Say("Atlas was kept; it is already archived."), "Nothing changed.");
            });
        var (loop, host, _) = Build(mind);
        var calls = 0;
        host.OnPropose = (m, _) => ++calls == 1
            ? MoveOutcome.Wait(Waits.Approval, new PolicyObserved(At, "p1", m.Action, PolicyObserved.NeedsApproval, []))
            : MoveOutcome.Of(new PolicyObserved(At, "p2", m.Action, PolicyObserved.Denied, ["The project is archived already."]));

        Assert.Null(await loop.RunAsync(CancellationToken.None));
        var result = await loop.ResumeAsync(MoveOutcome.Of(new ApprovalObserved(At, "p1", Actions.DeleteProject, false, "keep it")), CancellationToken.None);

        Assert.Equal("answered", result!.Outcome);
        Assert.Equal(["input", "move", "policy", "approval", "move", "policy", "move"], loop.Transcript.Select(o => o.Kind));
        Assert.Equal(2, loop.Proposals);
    }

    [Fact]
    public async Task AStreamingDelegateResumesTheLoopForOneStepPerPartialAndCanBeStopped()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Delegate("research", "Explain Nepal's time zone history in 200 words."), "Asking the research model.", ScriptedMind.Read(0.9, MindRead.NeedExternalReasoning))
            .Then(r =>
            {
                Assert.Equal(DelegateObserved.Partial, ((DelegateObserved)r.Transcript[^1]).Stage);
                return MindStep.Of(ScriptedMind.Say("The research model is writing about the 1986 offset change.", done: false), "The research model is writing about the 1986 offset change.");
            })
            .Then(r =>
            {
                Assert.Equal(2, r.Transcript.Count(o => o is DelegateObserved { Stage: DelegateObserved.Partial }));
                return MindStep.Of(ScriptedMind.Stop("It already answered the question."), "Stopping the research model; the answer is in.");
            })
            .Then(r =>
            {
                Assert.Equal(DelegateObserved.Stopped, ((DelegateObserved)r.Transcript[^1]).Stage);
                return MindStep.Of(ScriptedMind.Say("Nepal moved to UTC+05:45 in 1986."), "Done.");
            });
        var context = new MindContext { DelegateProfiles = ["research"] };
        var (loop, host, _) = Build(mind, context);

        Assert.Null(await loop.RunAsync(CancellationToken.None));
        Assert.Equal(Waits.Delegate, loop.WaitingFor);
        Assert.Equal(Decider.OfferDelegate, loop.Route!.Outcome);
        Assert.Contains(loop.Transcript, o => o is SystemObserved s && s.Text.StartsWith("Route: this looks too complex", StringComparison.Ordinal));

        // First partial: the mind narrates (say, not done) and the loop goes straight back to waiting.
        var afterFirst = await loop.ResumeAsync(MoveOutcome.Wait(Waits.Delegate, new DelegateObserved(At, "r1", "research", DelegateObserved.Partial, 600, "…in 1986 Nepal adopted")), CancellationToken.None);
        Assert.Null(afterFirst);
        Assert.Equal(LoopStatus.Waiting, loop.Status);
        Assert.Equal(Waits.Delegate, loop.WaitingFor);
        Assert.Single(host.Says);

        // Second partial: the mind stops the delegate; the stop is observed and the loop continues to the answer.
        var result = await loop.ResumeAsync(MoveOutcome.Wait(Waits.Delegate, new DelegateObserved(At, "r1", "research", DelegateObserved.Partial, 1400, "…UTC+05:45 since then")), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("answered", result!.Outcome);
        Assert.Contains("stop:delegate", host.Calls);
        Assert.Equal(4, result.Steps);
        Assert.Equal([Waits.Delegate, Waits.Delegate], host.Waits);
    }

    [Fact]
    public async Task ADelegateReturnEndsTheWaitAndTheMindReadsTheArtifact()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Delegate("research", "prompt"), "Delegating.")
            .Then(r => MindStep.Of(ScriptedMind.Tool("read_artifact", ("artifactId", ((DelegateObserved)r.Transcript[^1]).ArtifactId!)), "Reading the reply."))
            .Then(r => MindStep.Of(ScriptedMind.Say("Summary of the reply."), "Summarising."));
        var (loop, host, _) = Build(mind, new MindContext { DelegateProfiles = ["research"] });

        Assert.Null(await loop.RunAsync(CancellationToken.None));
        var result = await loop.ResumeAsync(MoveOutcome.Of(new DelegateObserved(At, "r1", "research", DelegateObserved.Returned, 2000, "The reply…", "a1")), CancellationToken.None);

        Assert.Equal("answered", result!.Outcome);
        Assert.Contains("tool:read_artifact", host.Calls);
        Assert.Equal(["step:delegate", "delegate:research", "waiting:delegate", "step:use_tool", "tool:read_artifact", "step:say", "said", "ended:answered"], host.Calls);
    }

    [Fact]
    public async Task UnavailableMovesAreObservedNotPerformed()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Delegate("research", "prompt"), "Delegating.")
            .Then(r => { Assert.Contains("No delegate profile is configured", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Build("world_clock", "We need a clock."), "Asking to build a clock."); })
            .Then(r => { Assert.Equal(BuildObserved.Unavailable, ((BuildObserved)r.Transcript[^1]).Stage); return MindStep.Of(ScriptedMind.Tool("weather"), "Checking the weather."); })
            .Then(r => { Assert.Contains("no tool named 'weather'", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Propose(Actions.RunShell, "x"), "Running a shell."); })
            .Then(r => { Assert.Contains("not an action you may propose", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Stop(), "Stopping."); })
            .Then(r => { Assert.Contains("Nothing is running", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Say("I cannot do that here: a world_clock tool would be needed."), "Explaining the gap."); });
        var (loop, host, _) = Build(mind);

        var result = await loop.RunAsync(CancellationToken.None);

        Assert.Equal("answered", result!.Outcome);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("delegate:", StringComparison.Ordinal) || c.StartsWith("build:", StringComparison.Ordinal) || c.StartsWith("tool:", StringComparison.Ordinal) || c.StartsWith("propose:", StringComparison.Ordinal) || c.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(0, loop.ToolCalls);
        Assert.Equal(0, loop.Proposals);
    }

    [Fact]
    public async Task TheFundamentalOperationFlagRefusesARiskyBuildAndPassesASafeOneWithItsDecision()
    {
        var risky = new MindRead("change my own policy", 0.5, [MindRead.NeedNewTool], 0, 0, new RiskRead(0.2, 0.9, 0, 0));
        var safe = new MindRead("tell the time anywhere", 0.3, [MindRead.NeedNewTool], 0, 0, new RiskRead(0, 0, 0, 0));
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Build("policy_editor", "edit policy"), "Building a policy editor.", risky)
            .Then(r => { Assert.Contains("Refused by the fundamental-operation flag", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Build("world_clock", "We need a clock.", "city", "local time"), "Building a world clock.", safe); })
            .Then(r => { Assert.Equal(BuildObserved.Promoted, ((BuildObserved)r.Transcript[^1]).Stage); return MindStep.Of(ScriptedMind.Say("Built the world clock."), "Done."); });
        var (loop, host, decider) = Build(mind, new MindContext { CanBuild = true });

        var result = await loop.RunAsync(CancellationToken.None);

        Assert.Equal("answered", result!.Outcome);
        Assert.Equal(["build:world_clock"], host.Calls.Where(c => c.StartsWith("build:", StringComparison.Ordinal)));
        Assert.Equal(Decider.Allow, host.LastFof!.Outcome);
        Assert.Equal(Decider.OfferBuild, loop.Route!.Outcome);   // the first read said new_tool and building is available
        var fofs = decider.Made.Where(d => d.Name == Decider.Fof).ToList();
        Assert.Equal([Decider.Refuse, Decider.Allow], fofs.Select(d => d.Outcome));
        Assert.Equal(0.9, fofs[0].Features["security"]);
        Assert.Equal(1, fofs[0].Features["self_directed"]);
    }

    [Fact]
    public async Task ASelfChangeProposalCarriesItsFofDecisionAndAnOverheardProposalIsSelfDirected()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Propose(Actions.UpdatePreference, "asked", ("key", "response.verbosity"), ("value", "terse")), "Changing verbosity.", new MindRead("terser", 0.1, [MindRead.NeedNone], 0, 0, new RiskRead(0.5, 0, 0, 0)))
            .Then(_ => MindStep.Of(ScriptedMind.Say("Done."), "Done."));
        var (loop, host, _) = Build(mind);
        await loop.RunAsync(CancellationToken.None);
        Assert.NotNull(host.LastFof);
        Assert.Equal(Decider.Approval, host.LastFof!.Outcome);   // core 0.5 ≥ 0.4: the user is asked even though the action is theirs

        var heard = new ScriptedMind()
            .Step(ScriptedMind.Propose(Actions.CreateDraftNote, "a decision was made", ("text", "Ship Friday"), ("type", "decision")), "Keeping a decision.", ScriptedMind.Read(0.1))
            .Then(_ => MindStep.Of(ScriptedMind.Wait("done"), "Done."));
        var (heardLoop, heardHost, _) = Build(heard, new MindContext { Actions = ActionCatalog.ForObserved }, origin: InputObserved.Heard, input: "We ship Friday, agreed.");
        var result = await heardLoop.RunAsync(CancellationToken.None);
        Assert.Equal("waited", result!.Outcome);
        Assert.NotNull(heardHost.LastFof);
        Assert.Equal(1, heardHost.LastFof!.Features["self_directed"]);
        Assert.Equal(Decider.Allow, heardHost.LastFof.Outcome);
    }

    [Fact]
    public async Task ABrokenReplyIsRetriedWithTheProblemOnTheTranscriptAndRepeatedFailureEndsTheTask()
    {
        var mind = new ScriptedMind()
            .Raw("not json at all")
            .Then(r =>
            {
                var system = Assert.IsType<SystemObserved>(r.Transcript[^1]);
                Assert.StartsWith("Your last reply was not usable: the reply was not valid JSON", system.Text);
                return MindStep.Of(ScriptedMind.Say("Hello."), "Hello.");
            });
        var (loop, host, decider) = Build(mind);
        var result = await loop.RunAsync(CancellationToken.None);
        Assert.Equal("answered", result!.Outcome);
        Assert.Equal(1, result.Steps);
        Assert.Equal(["step:failed", "step:say", "said", "ended:answered"], host.Calls);
        Assert.Equal([Decider.DoRetry], decider.Made.Where(d => d.Name == Decider.Retry).Select(d => d.Outcome));

        var hopeless = new ScriptedMind().Raw("x").Raw("y").Raw("z");
        var (loop2, host2, decider2) = Build(hopeless);
        var failed = await loop2.RunAsync(CancellationToken.None);
        Assert.Equal(LoopStatus.Failed, failed!.Status);
        Assert.Equal("mind_failed", failed.Outcome);
        Assert.StartsWith("Contract:", failed.Error);
        Assert.Equal([Decider.DoRetry, Decider.DoRetry, Decider.GiveUp], decider2.Made.Where(d => d.Name == Decider.Retry).Select(d => d.Outcome));

        var down = new ScriptedMind().Fail("Model unavailable: refused").Fail("Model unavailable: refused");
        var (loop3, _, _) = Build(down);
        var unavailable = await loop3.RunAsync(CancellationToken.None);
        Assert.Equal("mind_failed", unavailable!.Outcome);   // one model retry, then give up
        Assert.Equal(2, loop3.Transcript.Count);              // the input and the retry notice, nothing else
    }

    [Fact]
    public async Task BudgetsEndWhatTheMindDoesNot()
    {
        var looping = new ScriptedMind().Always(r => MindStep.Of(ScriptedMind.Tool("search", ("query", "attempt " + r.StepIndex)), "Searching."));
        var (loop, host, _) = Build(looping, budget: new LoopBudget(MaxSteps: 5, MaxToolCalls: 3));
        var result = await loop.RunAsync(CancellationToken.None);
        Assert.Equal(LoopStatus.Failed, result!.Status);
        Assert.Equal("step_budget", result.Outcome);
        Assert.Equal(3, loop.ToolCalls);
        Assert.Equal(3, host.Calls.Count(c => c == "tool:search"));
        Assert.Equal(2, loop.Transcript.Count(o => o is SystemObserved s && s.Text.Contains("tool budget", StringComparison.Ordinal)));

        // The same call again is not made: the loop observes the repetition and tells the mind what the call returned the first time.
        var repeating = new ScriptedMind()
            .Step(ScriptedMind.Tool("search", ("query", "Kathmandu")), "Searching.")
            .Step(ScriptedMind.Tool("search", ("query", " kathmandu ")), "Searching again.")
            .Then(r => { Assert.Contains("You already called search with these arguments", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Say("Nothing about Kathmandu in your notes."), "Answering."); });
        var (loop4, host4, _) = Build(repeating);
        Assert.Equal("answered", (await loop4.RunAsync(CancellationToken.None))!.Outcome);
        Assert.Equal(1, loop4.ToolCalls);
        Assert.Equal(1, host4.Calls.Count(c => c == "tool:search"));

        var chatty = new ScriptedMind()
            .Step(ScriptedMind.Say("Thinking…", done: false), "Thinking.")
            .Step(ScriptedMind.Say("Still thinking…", done: false), "Still thinking.")
            .Then(r => { Assert.Contains("narrated without acting", ((SystemObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Say("Done."), "Done."); });
        var (loop2, _, _) = Build(chatty);
        Assert.Equal("answered", (await loop2.RunAsync(CancellationToken.None))!.Outcome);
    }

    [Fact]
    public async Task AQuestionToTheUserWaitsForTheReplyAndWaitEndsAnOverheardWindowThatMeantNothing()
    {
        var mind = new ScriptedMind()
            .Step(ScriptedMind.Ask("Which project is this about?", "Atlas", "Backyard"), "Asking which project.")
            .Then(r => { Assert.Equal("Atlas", ((UserObserved)r.Transcript[^1]).Text); return MindStep.Of(ScriptedMind.Say("Filed under Atlas."), "Filed."); });
        var (loop, host, _) = Build(mind);
        Assert.Null(await loop.RunAsync(CancellationToken.None));
        Assert.Equal(Waits.User, loop.WaitingFor);
        var result = await loop.ResumeAsync(MoveOutcome.Of(new UserObserved(At, UserObserved.Reply, "Atlas")), CancellationToken.None);
        Assert.Equal("answered", result!.Outcome);

        var quiet = new ScriptedMind().Step(ScriptedMind.Wait("small talk"), "Nothing to do.", new MindRead("small talk", 0, [MindRead.NeedNone], 0.05, 0, RiskRead.None));
        var (heard, heardHost, _) = Build(quiet, origin: InputObserved.Heard, input: "Lovely weather today.");
        var waited = await heard.RunAsync(CancellationToken.None);
        Assert.Equal(LoopStatus.Done, waited!.Status);
        Assert.Equal("waited", waited.Outcome);
        Assert.Null(waited.Answer);
        Assert.Equal(["step:wait", "ended:waited"], heardHost.Calls);
    }

    [Fact]
    public async Task CancellationEndsTheLoopAsFailed()
    {
        using var cts = new CancellationTokenSource();
        var mind = new ScriptedMind().Then(_ => { cts.Cancel(); return MindStep.Of(ScriptedMind.Tool("list_projects"), "Listing."); });
        var (loop, host, _) = Build(mind);
        var result = await loop.RunAsync(cts.Token);
        Assert.Equal(LoopStatus.Failed, result!.Status);
        Assert.Equal("cancelled", result.Outcome);
        Assert.Contains("ended:cancelled", host.Calls);
    }
}

public class DeciderTests
{
    [Fact]
    public void RouteFollowsTheWeightsAndWhatIsAvailable()
    {
        var d = new Decider();
        Assert.Equal(Decider.Local, d.RouteFor(ScriptedMind.Read(0.2, MindRead.NeedLocalNotes), true, true).Outcome);
        var complex = d.RouteFor(ScriptedMind.Read(0.8, MindRead.NeedExternalReasoning), true, true);
        Assert.Equal(Decider.OfferDelegate, complex.Outcome);
        Assert.Equal(0.78, complex.Score);
        Assert.Contains("≥ 0.6", complex.Rationale);
        var noDelegates = d.RouteFor(ScriptedMind.Read(0.8, MindRead.NeedExternalReasoning), false, true);
        Assert.Equal(Decider.Local, noDelegates.Outcome);
        Assert.Contains("no delegate profile", noDelegates.Rationale);
        Assert.Equal(Decider.OfferBuild, d.RouteFor(ScriptedMind.Read(0.3, MindRead.NeedNewTool), true, true).Outcome);
        Assert.Equal(Decider.Local, d.RouteFor(ScriptedMind.Read(0.3, MindRead.NeedNewTool), true, false).Outcome);

        var tuned = DecisionSet.Default();
        tuned.Decisions[Decider.Route].Thresholds["delegate"] = 0.9;
        Assert.Equal(Decider.Local, new Decider(tuned).RouteFor(ScriptedMind.Read(0.8, MindRead.NeedExternalReasoning), true, true).Outcome);
    }

    [Fact]
    public void TheOtherDecisionsHaveTheirThresholds()
    {
        var made = new List<DecisionRecord>();
        var d = new Decider(null, made.Add);
        Assert.Equal(Decider.Allow, d.FofFor(null, Relay.Core.Mind.Move.Build, "x", selfDirected: false).Outcome);
        Assert.Equal(Decider.Approval, d.FofFor(new MindRead("", 0, [], 0, 0, new RiskRead(0, 0, 0.6, 0)), Relay.Core.Mind.Move.Build, "x", false).Outcome);   // loop 0.6 × 0.8 = 0.48
        Assert.Equal(Decider.Refuse, d.FofFor(new MindRead("", 0, [], 0, 0, new RiskRead(0, 0, 0, 0.7)), Relay.Core.Mind.Move.Propose, Actions.DeleteProject, true).Outcome); // 0.7 + 0.2 self-directed
        Assert.Equal(Decider.Auto, d.FilingFor(0.8).Outcome);
        Assert.Equal(Decider.Ask, d.FilingFor(0.5).Outcome);
        Assert.Equal(Decider.Inbox, d.FilingFor(0.2).Outcome);
        Assert.Equal(Decider.DoRetry, d.RetryFor(2, "contract").Outcome);
        Assert.Equal(Decider.GiveUp, d.RetryFor(3, "contract").Outcome);
        Assert.Equal(Decider.GiveUp, d.RetryFor(2, "model").Outcome);
        Assert.Equal(Decider.Surface, d.NarrateFor(500, 1).Outcome);
        Assert.Equal(Decider.Surface, d.NarrateFor(50, 9).Outcome);
        Assert.Equal(Decider.Hold, d.NarrateFor(50, 2).Outcome);
        Assert.Equal(Decider.Hold, d.NarrateFor(0, 30).Outcome);
        Assert.Equal(13, made.Count);
        Assert.All(made, r => Assert.False(string.IsNullOrWhiteSpace(r.Rationale)));
        Assert.True(made.All(r => r.Weights.Keys.Any(k => k.StartsWith("threshold.", StringComparison.Ordinal))));
    }

    [Fact]
    public void TheDecisionFileIsCreatedValidatedAndPartialEditsFallBackToDefaults()
    {
        using var tmp = new TempRoot();
        var set = DecisionSet.Load(tmp.Root, out var problems);
        Assert.Empty(problems);
        Assert.True(File.Exists(tmp.Root.DecisionsPath));
        Assert.True(set.Development);
        Assert.Equal(0.6, set.Spec(Decider.Route).T("delegate", 0));

        File.WriteAllText(tmp.Root.DecisionsPath, """{"schemaVersion":1,"development":false,"decisions":{"route":{"weights":{"complexity":0.9},"thresholds":{"delegate":0.5}}}}""");
        var edited = DecisionSet.Load(tmp.Root, out problems);
        Assert.Empty(problems);
        Assert.False(edited.Development);
        Assert.Equal(0.9, edited.Spec(Decider.Route).W("complexity"));
        Assert.Equal(0.75, edited.Spec(Decider.Filing).T("auto", 0));   // the missing spec comes from the defaults

        File.WriteAllText(tmp.Root.DecisionsPath, """{"decisions":{"filing":{"thresholds":{"auto":0.2,"ask":0.5}}}}""");
        var invalid = DecisionSet.Load(tmp.Root, out problems);
        Assert.Contains(problems, p => p.Contains("filing"));
        Assert.Equal(0.75, invalid.Spec(Decider.Filing).T("auto", 0));

        File.WriteAllText(tmp.Root.DecisionsPath, "{ not json");
        DecisionSet.Load(tmp.Root, out problems);
        Assert.Contains(problems, p => p.Contains("could not be parsed"));
    }

    [Fact]
    public void UsageLinesAreAppendedPerDayAndReadBack()
    {
        using var tmp = new TempRoot();
        var usage = new Relay.Core.Usage.UsageRecorder(tmp.Root);
        var at = new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.Zero);
        var line = new Relay.Core.Usage.UsageLine(at, "t1", InputObserved.Ask, "mind:test", Decider.Local, 0.2, [MindRead.NeedLocalNotes], 0.1, 2, 1, 0, 800, 120, 1500, "answered", new Dictionary<string, string> { [Decider.Route] = Decider.Local }, null, null);
        var path = usage.Record(line);
        usage.Record(line with { TaskId = "t2", Override = "delegate" });
        Assert.EndsWith("2026-09-09.jsonl", path);
        var back = usage.Read(at);
        Assert.Equal(["t1", "t2"], back.Select(l => l.TaskId));
        Assert.Equal("delegate", back[1].Override);
        Assert.Empty(usage.Read(at.AddDays(1)));
    }
}
