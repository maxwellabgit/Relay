using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// Preferences are typed and compiled: a request about how Relay should behave becomes proposals
/// with closed keys, approved by the user, applied as reversible change sets, and compiled into the
/// prompt fragment, the generation limits, the display policy, and the standing grants.
/// </summary>
public class PreferenceTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    // ----------------------------------------------------------------------------------------
    // Compilation
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ResponsePreferences.Minimalist, 160, 240, "one short sentence")]
    [InlineData(ResponsePreferences.Concise, 400, 700, "at most three short sentences")]
    [InlineData(ResponsePreferences.Normal, 900, 2000, "plainly and completely")]
    public void VerbosityCompilesIntoLimitsAndAStyleLine(string verbosity, int tokens, int chars, string styleFragment)
    {
        var compiled = PreferenceCompiler.Compile(new UserPreferences { Response = { Verbosity = verbosity, PromptLines = { "Never use bullet lists.", "  ", "Cite the note id." } } });
        Assert.Equal(tokens, compiled.MaxAnswerTokens);
        Assert.Equal(chars, compiled.MaxAnswerChars);
        Assert.Contains(styleFragment, compiled.PromptFragment);
        Assert.EndsWith("Never use bullet lists. Cite the note id.", compiled.PromptFragment);        // approved lines follow the style, blanks dropped
    }

    [Fact]
    public void DisplayRetentionAndSourcePreferencesCompileToTypedValues()
    {
        var prefs = new UserPreferences
        {
            Display = { AlwaysShowTerms = { "CAD", " cad ", "SLA", "" }, MaxAlertsPer10Minutes = 2, MaxResultsPer5Minutes = 4, CooldownSeconds = 120 },
            Retention = { BufferSeconds = 60, ExcerptMaxSeconds = 20, MaxRetainedFraction = 0.2 },
            Sources = { AllowOnlineSearch = true },
            Filing = { Grants = { new StandingGrant { GrantId = "G1", Action = Actions.RouteNote, ProjectId = "P1", NoteType = NoteTypes.Decision } } },
        };
        var compiled = PreferenceCompiler.Compile(prefs);
        Assert.Equal(["CAD", "SLA"], compiled.WatchedTerms);                                            // trimmed, case-insensitively distinct, blanks dropped
        Assert.Equal(2, compiled.MaxAlertsPer10Minutes);
        Assert.Equal(4, compiled.MaxResultsPer5Minutes);
        Assert.Equal(TimeSpan.FromSeconds(120), compiled.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(60), compiled.Buffer);
        Assert.Equal(20, compiled.ExcerptMaxSeconds);
        Assert.Equal(0.2, compiled.MaxRetainedFraction);
        Assert.True(compiled.AllowOnlineSearch);
        var grant = Assert.Single(compiled.Grants);
        Assert.True(grant.Covers(Actions.RouteNote, new Dictionary<string, string> { ["projectId"] = "P1", ["type"] = NoteTypes.Decision }));
        Assert.False(grant.Covers(Actions.RouteNote, new Dictionary<string, string> { ["projectId"] = "P1", ["type"] = NoteTypes.Idea }));
        Assert.False(grant.Covers(Actions.RouteNote, new Dictionary<string, string> { ["projectId"] = "P2", ["type"] = NoteTypes.Decision }));
        Assert.False(grant.Covers(Actions.SupersedeNote, new Dictionary<string, string> { ["projectId"] = "P1", ["type"] = NoteTypes.Decision }));
    }

    [Fact]
    public void InvalidPreferencesAreNamedAndAStandingGrantCanNeverCoverTheAlwaysAskActions()
    {
        var prefs = new UserPreferences
        {
            Response = { Verbosity = "chatty" },
            Display = { MaxAlertsPer10Minutes = 99, CooldownSeconds = -1 },
            Retention = { BufferSeconds = 5, ExcerptMaxSeconds = 500, MaxRetainedFraction = 0 },
            Filing = { Grants = { new StandingGrant { Action = Actions.DeleteProject }, new StandingGrant { Action = Actions.UpdatePreference }, new StandingGrant { Action = Actions.ModelRequest }, new StandingGrant { Action = "" } } },
        };
        var problems = prefs.Validate();
        Assert.Contains(problems, p => p.Contains("response.verbosity"));
        Assert.Contains(problems, p => p.Contains("maxAlertsPer10Minutes"));
        Assert.Contains(problems, p => p.Contains("cooldownSeconds"));
        Assert.Contains(problems, p => p.Contains("bufferSeconds"));
        Assert.Contains(problems, p => p.Contains("excerptMaxSeconds"));
        Assert.Contains(problems, p => p.Contains("maxRetainedFraction"));
        Assert.Contains(problems, p => p.Contains("delete_project") && p.Contains("fresh approval"));
        Assert.Contains(problems, p => p.Contains("update_preference"));
        Assert.Contains(problems, p => p.Contains("model.request"));
        Assert.Contains(problems, p => p.Contains("no action"));
    }

    // ----------------------------------------------------------------------------------------
    // The grammar
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("update our preferences to always display concise text", ResponsePreferences.Concise)]
    [InlineData("keep responses concise", ResponsePreferences.Concise)]
    [InlineData("be brief", ResponsePreferences.Concise)]
    [InlineData("make your answers shorter", ResponsePreferences.Concise)]
    [InlineData("from now on, keep it short", ResponsePreferences.Concise)]
    [InlineData("answer minimally", ResponsePreferences.Minimalist)]
    [InlineData("give me one-liners", ResponsePreferences.Minimalist)]
    [InlineData("set the response style to minimalist", ResponsePreferences.Minimalist)]
    [InlineData("respond normally", ResponsePreferences.Normal)]
    [InlineData("answer in detail", ResponsePreferences.Normal)]
    [InlineData("change my settings to full answers", ResponsePreferences.Normal)]
    public void AStyleRequestYieldsTheTypedPreferenceAndThePromptLine(string instruction, string verbosity)
    {
        using var h = new Harness(_tmp.Root).Start();
        var plan = Plan(h, instruction);
        Assert.True(plan.Understood);
        Assert.Equal("rules", plan.Producer);
        Assert.Equal(2, plan.Proposals.Count);
        var pref = Assert.Single(plan.Proposals, p => p.Action == Actions.UpdatePreference);
        Assert.Equal("response.verbosity", pref.Target["key"]);
        Assert.Equal(verbosity, pref.Target["value"]);
        var prompt = Assert.Single(plan.Proposals, p => p.Action == Actions.UpdatePrompt);
        Assert.Equal("planner", prompt.Target["name"]);
        Assert.Equal(RuleBasedOrchestrator.PromptLine(instruction), prompt.Target["content"]);
        Assert.All(plan.Proposals, p => Assert.True(p.RequiresApproval));
    }

    [Theory]
    [InlineData("update our preferences to always display concise text", "Always display concise text.")]
    [InlineData("keep responses concise", "Keep responses concise.")]
    [InlineData("From now on, be brief!", "Be brief.")]
    [InlineData("please set my style to minimalist.", "Minimalist.")]
    public void ThePromptLineIsTheUsersOwnWordingCleaned(string instruction, string line)
        => Assert.Equal(line, RuleBasedOrchestrator.PromptLine(instruction));

    [Fact]
    public void ThePlannerFragmentGrowsByOneLineAndNeverRepeatsItself()
    {
        Assert.Equal("Keep responses concise.", RuleBasedOrchestrator.ComposeFragment(null, "Keep responses concise."));
        Assert.Equal("Cite ids.\nKeep responses concise.", RuleBasedOrchestrator.ComposeFragment("Cite ids.\n", "Keep responses concise."));
        Assert.Null(RuleBasedOrchestrator.ComposeFragment("Cite ids.\nkeep responses concise.", "Keep responses concise."));   // already there
        Assert.Null(RuleBasedOrchestrator.ComposeFragment(new string('x', 1995), "Keep responses concise."));                 // over the policy limit
    }

    [Theory]
    [InlineData("always show what CAD means", "CAD")]
    [InlineData("always show CAD", "CAD")]
    [InlineData("pin the definition of SLA", "SLA")]
    [InlineData("always display what OKR stands for", "OKR")]
    [InlineData("always tell me what \"TTFB\" means when it comes up", "TTFB")]
    public void PinningATermProposesTheDisplayPreference(string instruction, string term)
    {
        using var h = new Harness(_tmp.Root).Start();
        var plan = Plan(h, instruction);
        Assert.True(plan.Understood);
        var p = Assert.Single(plan.Proposals);
        Assert.Equal(Actions.UpdatePreference, p.Action);
        Assert.Equal("display.alwaysShow", p.Target["key"]);
        Assert.Equal(term, p.Target["value"]);
    }

    [Theory]
    [InlineData("create project Concise", Actions.CreateProject)]
    [InlineData("remember that the concise version ships first", Actions.CreateDraftNote)]
    public void TheShapingGrammarDoesNotCaptureOrdinaryCommands(string instruction, string action)
    {
        using var h = new Harness(_tmp.Root).Start();
        var plan = Plan(h, instruction);
        Assert.Equal(action, Assert.Single(plan.Proposals).Action);
    }

    [Fact]
    public void AnObservedTaskCannotShapeRelayEvenIfAPlannerProposesIt()
    {
        var mind = new ListeningMind().When("concise", "improve", "Keep responses concise from now on.", topic: "style");
        var planner = new CannedOrchestrator().Otherwise((request, _) => new TurnPlan(true, "Make responses concise", [], null, [],
            [new Proposal(Relay.Core.Ids.Ulid.NewUlid(request.At), Actions.UpdatePreference, "overheard", new Dictionary<string, string> { ["key"] = "response.verbosity", ["value"] = "concise" }, [request.SourceEventId], [], Risks.ControlledWrite, true, Producers.Model)],
            "canned"));
        using var s = Scenario.New(_tmp, x => { x.Orchestrator.Mode = OrchestratorSettings.Rules; x.Listening.Enabled = true; },
                mind: mind, orchestrator: new CompositeOrchestrator(new RuleBasedOrchestrator(), planner)).WithWorkspace()
            .Do("Start from normal", c => Assert.True(c.UpdatePreference("response.verbosity", "normal")))
            .StartListening()
            .Listen("Honestly I wish these answers were more concise.")
            .ExpectTask(TaskKind.Improve, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAnyProposal(Actions.UpdatePreference, "denied")
            .ExpectPreference("response.verbosity", "normal");
        var task = s.FindTask(TaskKind.Improve)!;
        Assert.Contains(task.Proposals[0].Reasons, r => r.Contains("only be proposed from a direct request"));
        Assert.Equal("canned", task.Producer);                                                          // the rules grammar declined an observed request; the fallback planned it
    }

    // ----------------------------------------------------------------------------------------
    // The scenarios
    // ----------------------------------------------------------------------------------------

    /// <summary>The README's "concise" scenario: two proposals, two change sets, both compiled in, both revertible.</summary>
    [Fact]
    public void UpdatingPreferencesToConciseTextIsTwoApprovedRevertibleChangeSets()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Do("Start from normal", c => Assert.True(c.UpdatePreference("response.verbosity", "normal")))
            .ExpectPreference("response.verbosity", "normal")
            .Command("update our preferences to always display concise text")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.UpdatePreference, "pending")
            .ExpectProposal(Actions.UpdatePrompt, "pending");

        var pref = Assert.Single(s.Response.Proposals, p => p.Action == Actions.UpdatePreference);
        var prompt = Assert.Single(s.Response.Proposals, p => p.Action == Actions.UpdatePrompt);
        Assert.Equal("Change preference response.verbosity", pref.Title);
        Assert.Contains("concise", pref.Detail);
        Assert.Contains("reversible change set", pref.Detail);
        Assert.Equal("Change the 'planner' prompt fragment", prompt.Title);
        Assert.Contains("Always display concise text.", prompt.Detail);
        Assert.True(pref.Editable);
        Assert.True(prompt.Editable);

        var before = s.H.ChangeSets.All().Count;
        s.ApproveAll()
            .ExpectState(RelayState.Completed)
            .ExpectProposal(Actions.UpdatePreference, "executed")
            .ExpectProposal(Actions.UpdatePrompt, "executed")
            .ExpectPreference("response.verbosity", "concise")
            .ExpectEvent(EventTypes.ChangeSetApplied, before + 2);

        // Compiled in: limits, the style line, the approved wording.
        var compiled = s.H.Preferences.Compiled();
        Assert.Equal(700, compiled.MaxAnswerChars);
        Assert.Contains("at most three short sentences", compiled.PromptFragment);
        var fragmentPath = Path.Combine(s.H.Root.PromptsDirectory, "planner.md");
        Assert.Equal("Always display concise text.", File.ReadAllText(fragmentPath).Trim());
        Assert.Equal("Always display concise text.", s.H.SelfChange.PromptFragment("planner"));
        var systemPrompt = ModelOrchestrator.SystemPrompt(s.H.PlannerContext());
        Assert.Contains("at most three short sentences", systemPrompt);
        Assert.Contains("Additional instructions approved by the user: Always display concise text.", systemPrompt);

        // Both are change sets with a before image, listed in the snapshot, and revertible one at a time.
        var sets = s.Snap.ChangeSets.Where(c => !c.Reverted).OrderBy(c => c.AppliedAt).ToList();
        Assert.Equal(3, sets.Count);                                                                     // normal (setup), concise, planner line
        var preferenceSet = sets.Single(c => c.Kind == "preference" && c.TaskId == s.Response.TaskId);
        var promptSet = sets.Single(c => c.Kind == "prompt");
        Assert.Equal("planner.md", promptSet.File);
        Assert.Equal("preferences.json", preferenceSet.File);

        s.Do("Revert the prompt line", c => Assert.True(c.RevertChangeSet(promptSet.ChangeSetId)))
            .ExpectEvent(EventTypes.ChangeSetReverted);
        Assert.False(File.Exists(fragmentPath));                                                        // there was no fragment before: reverting removes it
        Assert.Null(s.H.SelfChange.PromptFragment("planner"));
        Assert.DoesNotContain("Additional instructions", ModelOrchestrator.SystemPrompt(s.H.PlannerContext()));
        s.ExpectPreference("response.verbosity", "concise");                                             // the other change set stands

        s.Do("Revert the preference", c => Assert.True(c.RevertChangeSet(preferenceSet.ChangeSetId)))
            .ExpectPreference("response.verbosity", "normal")
            .ExpectEvent(EventTypes.ChangeSetReverted, 2);
        Assert.Equal(2000, s.H.Preferences.Compiled().MaxAnswerChars);
        Assert.False(s.C.RevertChangeSet(preferenceSet.ChangeSetId));                                     // twice is refused
        Assert.Contains("Already reverted", s.Snap.Notice);
    }

    [Fact]
    public void RepeatingAStyleAlreadyInThePromptProposesOnlyThePreference()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("keep responses concise").ApproveAll().ExpectState(RelayState.Completed).Dismiss()
            .Do("Back to normal", c => Assert.True(c.UpdatePreference("response.verbosity", "normal")))
            .Command("keep responses concise")
            .ExpectProposal(Actions.UpdatePreference, "pending");
        Assert.DoesNotContain(s.Response.Proposals, p => p.Action == Actions.UpdatePrompt);               // the line is already in the fragment
        Assert.Contains("Current style", s.Response.Answer);
    }

    [Fact]
    public void PinningAndUnpinningATermGoThroughApprovedChangeSets()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("always show what CAD means")
            .ExpectProposal(Actions.UpdatePreference, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectPreference("display.alwaysShow", "CAD")
            .ExpectEvent(EventTypes.ChangeSetApplied)
            .Dismiss()
            .Command("always show CAD")                                                                 // already pinned: an answer, nothing proposed
            .ExpectState(RelayState.Completed)
            .ExpectNoProposals()
            .ExpectAnswerContains("already pinned")
            .Dismiss()
            .Command("stop showing SLA")                                                                // not pinned: an answer that lists what is
            .ExpectNoProposals()
            .ExpectAnswerContains("Pinned terms: CAD")
            .Dismiss()
            .Command("stop showing CAD")
            .ExpectProposal(Actions.UpdatePreference, "pending")
            .Approve()
            .ExpectPreference("display.alwaysShow", "");
        Assert.Equal(2, s.Snap.ChangeSets.Count);
        Assert.Empty(s.H.Preferences.Compiled().WatchedTerms);
    }

    /// <summary>"File Atlas decisions without asking": a standing grant files at the Review threshold; revoking it restores the question.</summary>
    [Fact]
    public void AStandingGrantFilesWithoutAskingAndIsRevocable()
    {
        // Nothing files automatically in this session unless a grant says so.
        using var s = Scenario.New(_tmp, configure: x => x.Orchestrator.AutoRouteThreshold = 0.99).WithWorkspace()
            .Command("create project Atlas").Approve().Dismiss()
            .Note("Decision: the Atlas beta ships on October 14.")
            .Dismiss();
        Assert.Empty(ProjectNoteStore.ReadAll(s.H.Registry.FindActive("atlas")!.RootPath).Notes);
        var waiting = Assert.Single(s.Snap.Inbox);                                                        // moderately confident routing waits for the user
        Assert.True(waiting.HasSuggestions);
        Assert.Equal("atlas", waiting.Candidates[0].Slug);
        var routingBefore = s.Snap.Inbox.Count;

        s.Command("file Atlas decisions without asking")
            .ExpectProposal(Actions.UpdatePreference, "pending");
        var p = Assert.Single(s.Response.Proposals);
        Assert.Equal("filing.grant", p.Target["key"]);
        Assert.Equal(Actions.RouteNote, p.Target["value"]);
        Assert.Equal(NoteTypes.Decision, p.Target["noteType"]);
        Assert.Equal(s.H.Registry.FindActive("atlas")!.Id, p.Target["projectId"]);
        s.Approve().ExpectState(RelayState.Completed).ExpectEvent(EventTypes.GrantApplied).Dismiss();
        var grant = Assert.Single(s.H.Preferences.Compiled().Grants);
        Assert.Equal(NoteTypes.Decision, grant.NoteType);
        s.ExpectPreference("filing.grant", $"{Actions.RouteNote}:{grant.ProjectId}/{NoteTypes.Decision}");

        // Same kind of note: filed without asking, and the ledger says which grant did it.
        s.Note("Decision: the Atlas launch review is on October 10.")
            .ExpectEvent(EventTypes.GrantApplied, 2)
            .Dismiss();
        var notes = ProjectNoteStore.ReadAll(s.H.Registry.FindActive("atlas")!.RootPath).Notes.Select(n => n.Note).ToList();
        var filed = Assert.Single(notes);
        Assert.Equal(NoteTypes.Decision, filed.Type);
        Assert.Contains("launch review", filed.Body);
        var applied = s.H.Last(EventTypes.GrantApplied)!;
        Assert.Equal(grant.GrantId, applied.DataString("grantId"));
        Assert.Equal(filed.Id, applied.DataString("noteId"));
        Assert.Equal(routingBefore, s.Snap.Inbox.Count);                                                  // nothing new waits in the inbox

        // A different note type is not covered: it still waits.
        s.Note("Idea: an Atlas onboarding video with subtitles for new customers.").Dismiss();
        Assert.Equal(routingBefore + 1, s.Snap.Inbox.Count);
        Assert.Single(ProjectNoteStore.ReadAll(s.H.Registry.FindActive("atlas")!.RootPath).Notes);

        // Revoke: the grant is removed by an approved change set and decisions wait again.
        s.Command("stop filing Atlas decisions without asking")
            .ExpectProposal(Actions.UpdatePreference, "pending");
        Assert.Equal("filing.revoke", Assert.Single(s.Response.Proposals).Target["key"]);
        Assert.Equal(grant.GrantId, Assert.Single(s.Response.Proposals).Target["value"]);
        s.Approve().ExpectState(RelayState.Completed).ExpectPreference("filing.grant", "").Dismiss()
            .Note("Decision: Atlas pricing goes public next spring with three tiers.").Dismiss();
        Assert.Equal(routingBefore + 2, s.Snap.Inbox.Count);
        Assert.Single(ProjectNoteStore.ReadAll(s.H.Registry.FindActive("atlas")!.RootPath).Notes);
        Assert.Empty(s.H.Preferences.Compiled().Grants);

        s.Command("stop filing Atlas decisions without asking").ExpectNoProposals().ExpectAnswerContains("already asks");
    }

    [Fact]
    public void AGrantForAnUnknownProjectOrTheWrongPhrasingIsAnsweredNotProposed()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("file Backyard decisions without asking")
            .ExpectNoProposals()
            .ExpectAnswerContains("No active project matches 'Backyard'");
    }

    // ----------------------------------------------------------------------------------------

    private static TurnPlan Plan(Harness h, string instruction)
    {
        var request = new TurnRequest("T1", "C1", "E1", instruction, h.Clock.UtcNow);
        return new RuleBasedOrchestrator().PlanAsync(request, h.PlannerContext(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _tmp.Dispose();
}
