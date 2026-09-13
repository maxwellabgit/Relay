using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.SelfChange;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// Preferences are typed and compiled: a request about how Relay should behave becomes proposals with
/// closed keys, each arguing for itself, approved by the user, applied as reversible change sets, and
/// compiled into the prompt the mind is given, the answer limits, the display policy, and the standing grants.
/// </summary>
public class PreferenceTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    /// <summary>The mind runs every task; nothing here leaves the machine.</summary>
    private static void MindMode(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    /// <summary>The user's own wording, kept as an approved instruction beside the typed preference.</summary>
    private const string ConciseLine = "Always display concise text.";

    /// <summary>
    /// The improvement contract every self-change carries: what the user gains, what it may write, how far it
    /// reaches, and how to tell afterwards that it worked. Policy requires all four of an improve task.
    /// </summary>
    private static (string Key, string Value)[] Contract(string benefit, string permissions, string scope, string acceptance)
        => [("benefit", benefit), ("permissions", permissions), ("scope", scope), ("acceptance", acceptance)];

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
    // The improvement contract
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A change Relay proposes to itself is an argument, not a setting: it says what the user gains, what it may
    /// write, how far it reaches, and how to tell afterwards that it worked. An improve task refuses a self-change
    /// that leaves any of the four unsaid; the user changing their own setting owes no argument, and neither does
    /// any other kind of task, where the four are optional but still bounded.
    /// </summary>
    [Fact]
    public void AnImprovementMustStateItsBenefitPermissionsScopeAndAcceptance()
    {
        using var h = new Harness(_tmp.Root).Start();
        Decision Decide(TaskKind kind, string proposedBy, params (string Key, string Value)[] target)
            => PolicyEngine.Decide(
                new Proposal("01PROPOSAL0000000000000000", Actions.UpdatePreference, "shorter answers are quicker to read",
                    target.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal), ["01SOURCE000000000000000000"], [], Risks.ControlledWrite, true, proposedBy),
                new PolicyWorld
                {
                    Registry = h.Registry, Roots = h.Roots, DataRoot = h.Root,
                    DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false,
                    Origin = TaskOrigin.Direct, Kind = kind,
                });
        (string Key, string Value)[] concise = [("key", "response.verbosity"), ("value", ResponsePreferences.Concise)];
        var contract = Contract("Answers are shorter to read", "Writes config\\preferences.json (change set)",
            "One typed preference: response.verbosity", "Compiled preferences report 'concise'");

        var unargued = Decide(TaskKind.Improve, Producers.Mind, concise);
        Assert.Equal(DecisionOutcome.Deny, unargued.Outcome);
        Assert.All(PolicyEngine.ContractKeys, key => Assert.Contains(unargued.Reasons, r => r.Contains("target." + key, StringComparison.Ordinal)));

        Assert.Equal(DecisionOutcome.NeedsApproval, Decide(TaskKind.Improve, Producers.Mind, [.. concise, .. contract]).Outcome);
        Assert.Equal(DecisionOutcome.NeedsApproval, Decide(TaskKind.Improve, Producers.User, concise).Outcome);      // the user's own setting is configuration
        Assert.Equal(DecisionOutcome.NeedsApproval, Decide(TaskKind.Answer, Producers.Mind, concise).Outcome);       // only an improvement owes the argument

        var wordy = Decide(TaskKind.Answer, Producers.Mind, [.. concise, .. Contract(new string('b', 401), "p", "s", "a")]);
        Assert.Equal(DecisionOutcome.Deny, wordy.Outcome);
        Assert.Contains(wordy.Reasons, r => r.Contains("target.benefit is longer than 400 characters", StringComparison.Ordinal));
    }

    /// <summary>
    /// A self-change's stated scope has to be true. An approved prompt fragment is added to the mind's prompt;
    /// it can never become the constitution, which is what judges proposals like it in the first place — so no
    /// name a proposal may write is the name the constitution is read from, and a fragment leaves it standing.
    /// </summary>
    [Fact]
    public void AnApprovedPromptFragmentIsAddedToTheConstitutionAndCanNeverReplaceIt()
    {
        Assert.DoesNotContain(MindPrompt.ConstitutionName, SelfChangeRuntime.PromptNames);
        Assert.Contains(MindPrompt.PromptName, SelfChangeRuntime.PromptNames);
        Assert.NotEqual(MindPrompt.ConstitutionName, MindPrompt.PromptName);

        var mind = new ScriptedMind()
            .Step(ScriptedMind.Propose(Actions.UpdatePrompt, "you asked for no bullet lists",
                [("name", MindPrompt.PromptName), ("content", "Never use bullet lists."), .. Contract("Prose reads faster", "Writes one prompt fragment", "One fragment", "The next answer has no bullets")]),
                "Proposing the instruction")
            .Always(_ => MindStep.Of(ScriptedMind.Say("That instruction is in place."), "The fragment is approved."));

        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Command("never use bullet lists")
            .ExpectProposal(Actions.UpdatePrompt, "pending")
            .Approve()
            .ExpectEvent(EventTypes.ChangeSetApplied);

        // The fragment is on the record and in the prompt, as an addition the user approved.
        var prompt = MindPrompt.System(s.H.MindContext());
        Assert.Contains("Additional instructions approved by the user: Never use bullet lists.", prompt);
        // And the constitution is untouched: every rule that governs a proposal is still there.
        Assert.StartsWith(MindPrompt.DefaultConstitution, prompt);
        Assert.Contains("Never invent ids", prompt);
        Assert.Contains("You never act directly", prompt);
    }

    // ----------------------------------------------------------------------------------------
    // The scenarios
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The README's "concise" scenario: the typed preference and the user's own wording are two self-changes,
    /// each argued for, each approved on its own, each a change set that can be reverted without the other.
    /// </summary>
    [Fact]
    public void UpdatingPreferencesToConciseTextIsTwoApprovedRevertibleChangeSets()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.UpdatePreference, "You asked for concise answers; the typed preference sets the length limits and the style line.",
                    [("key", "response.verbosity"), ("value", ResponsePreferences.Concise),
                     .. Contract("Answers are shorter to read and cost fewer tokens per task",
                         "Writes config\\preferences.json (change set)",
                         "One typed preference: response.verbosity; the answer limits and the style line follow from it",
                         "Compiled preferences report 'concise' and the next answer stays within its character limit")]),
                "Proposing concise answers", Read(0.2))
            .Step(Propose(Actions.UpdatePrompt, "Your own wording is kept as an approved instruction, so answers follow it verbatim and not only as a setting.",
                    [("name", MindPrompt.PromptName), ("content", ConciseLine),
                     .. Contract("Relay is told the style in your own words",
                         "Writes config\\prompts\\mind.md (change set)",
                         $"One added line: \"{ConciseLine}\"",
                         "The system prompt carries the line; reverting the change set removes it")]),
                "Proposing the instruction in your own words")
            .Always(_ => MindStep.Of(Say("Answers are concise from now on, in your own words."), "Concise from now on"));

        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Do("Start from normal", c => Assert.True(c.UpdatePreference("response.verbosity", "normal")))
            .ExpectPreference("response.verbosity", "normal")
            .Command("update our preferences to always display concise text")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.UpdatePreference, "pending");

        var pref = Assert.Single(s.Response.Proposals, p => p.Action == Actions.UpdatePreference);
        Assert.Equal("Change preference response.verbosity", pref.Title);
        Assert.Contains("concise", pref.Detail);
        Assert.Contains("reversible change set", pref.Detail);
        Assert.Contains("Benefit: Answers are shorter to read", pref.Detail);              // the card states the whole argument, not only the new value
        Assert.Contains("Acceptance: Compiled preferences report 'concise'", pref.Detail);
        Assert.True(pref.Editable);

        var before = s.H.ChangeSets.All().Count;
        s.Approve(Actions.UpdatePreference)
            .ExpectProposal(Actions.UpdatePreference, "executed")
            .ExpectPreference("response.verbosity", ResponsePreferences.Concise)
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)                                       // one approval at a time: the second self-change is put on its own
            .ExpectProposal(Actions.UpdatePrompt, "pending");

        var prompt = Assert.Single(s.Response.Proposals, p => p.Action == Actions.UpdatePrompt);
        Assert.Equal($"Change the '{MindPrompt.PromptName}' prompt fragment", prompt.Title);
        Assert.Contains(ConciseLine, prompt.Detail);
        Assert.True(prompt.Editable);

        s.Approve(Actions.UpdatePrompt)
            .ExpectState(RelayState.Ready)
            .ExpectProposal(Actions.UpdatePrompt, "executed")
            .ExpectEvent(EventTypes.ChangeSetApplied, before + 2);
        Assert.Equal(mind.Name, s.Response.Producer);

        // Compiled in: limits, the style line, the approved wording.
        var compiled = s.H.Preferences.Compiled();
        Assert.Equal(700, compiled.MaxAnswerChars);
        Assert.Contains("at most three short sentences", compiled.PromptFragment);
        var fragmentPath = Path.Combine(s.H.Root.PromptsDirectory, MindPrompt.PromptName + ".md");
        Assert.Equal(ConciseLine, File.ReadAllText(fragmentPath).Trim());
        Assert.Equal(ConciseLine, s.H.SelfChange.PromptFragment(MindPrompt.PromptName));
        var systemPrompt = MindPrompt.System(s.H.MindContext());
        Assert.Contains("at most three short sentences", systemPrompt);
        Assert.Contains("Additional instructions approved by the user: " + ConciseLine, systemPrompt);

        // Both are change sets with a before image, listed in the snapshot, and revertible one at a time.
        var sets = s.Snap.ChangeSets.Where(c => !c.Reverted).OrderBy(c => c.AppliedAt).ToList();
        Assert.Equal(3, sets.Count);                                                        // normal (setup), concise, the approved line
        var preferenceSet = sets.Single(c => c.Kind == "preference" && c.TaskId == s.Response.TaskId);
        var promptSet = sets.Single(c => c.Kind == "prompt");
        Assert.Equal(MindPrompt.PromptName + ".md", promptSet.File);
        Assert.Equal("preferences.json", preferenceSet.File);

        s.Do("Revert the approved line", c => Assert.True(c.RevertChangeSet(promptSet.ChangeSetId)))
            .ExpectEvent(EventTypes.ChangeSetReverted);
        Assert.False(File.Exists(fragmentPath));                                            // there was no fragment before: reverting removes it
        Assert.Null(s.H.SelfChange.PromptFragment(MindPrompt.PromptName));
        Assert.DoesNotContain("Additional instructions", MindPrompt.System(s.H.MindContext()));
        s.ExpectPreference("response.verbosity", ResponsePreferences.Concise);               // the other change set stands

        s.Do("Revert the preference", c => Assert.True(c.RevertChangeSet(preferenceSet.ChangeSetId)))
            .ExpectPreference("response.verbosity", "normal")
            .ExpectEvent(EventTypes.ChangeSetReverted, 2);
        Assert.Equal(2000, s.H.Preferences.Compiled().MaxAnswerChars);
        Assert.False(s.C.RevertChangeSet(preferenceSet.ChangeSetId));                        // twice is refused
        Assert.Contains("Already reverted", s.Snap.Notice);
    }

    /// <summary>
    /// The verbosity preference is not advice: whatever the mind writes, the answer the user reads is cut to the
    /// length they chose, and the feed says it was cut rather than leaving a sentence to end mid-word unexplained.
    /// </summary>
    [Fact]
    public void AnAnswerLongerThanThePreferredLengthIsCutAndTheFeedSaysSo()
    {
        var mind = new ScriptedMind().Step(Say(new string('a', 900)), "Answered at length", Read(0.1));
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Do("Ask for one-liners", c => Assert.True(c.UpdatePreference("response.verbosity", ResponsePreferences.Minimalist)))
            .Command("what do you know about the beta?")
            .ExpectState(RelayState.Ready);
        Assert.Equal(241, s.Response.Answer!.Length);                                        // 240 characters and the ellipsis that says there was more
        Assert.EndsWith("…", s.Response.Answer);
        Assert.Contains(s.Response.Steps, step => step.Contains("240 chars"));
    }

    [Fact]
    public void PinningAndUnpinningATermGoThroughApprovedChangeSets()
    {
        var mind = new ScriptedMind().Always(request => request.Transcript[^1] switch
        {
            InputObserved pin when pin.Text.Contains("always show", StringComparison.OrdinalIgnoreCase)
                => MindStep.Of(Propose(Actions.UpdatePreference, "You asked for CAD to be explained whenever it comes up.",
                    [("key", "display.alwaysShow"), ("value", "CAD"),
                     .. Contract("'CAD' is defined on screen the moment it is heard, without asking",
                         "Writes config\\preferences.json (change set); reads local sources only",
                         "One watched term; the pinned card refreshes in place",
                         "Hearing 'CAD' while listening shows a pinned definition within one pass")]),
                    "Proposing to pin CAD", Read(0.2)),
            InputObserved => MindStep.Of(Propose(Actions.UpdatePreference, "You asked to stop pinning CAD.",
                    [("key", "display.stopShowing"), ("value", "CAD"),
                     .. Contract("One less thing on screen", "Writes config\\preferences.json (change set)",
                         "Removes one watched term", "Hearing 'CAD' no longer produces a pinned result")]),
                "Proposing to unpin CAD", Read(0.2)),
            _ => MindStep.Of(Say("Your pinned terms are what you asked for."), "Done"),
        });

        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Command("always show what CAD means")
            .ExpectProposal(Actions.UpdatePreference, "pending")
            .Approve()
            .ExpectState(RelayState.Ready)
            .ExpectPreference("display.alwaysShow", "CAD")
            .ExpectEvent(EventTypes.ChangeSetApplied)
            .Dismiss()
            .Command("stop showing CAD")
            .ExpectProposal(Actions.UpdatePreference, "pending")
            .Approve()
            .ExpectState(RelayState.Ready)
            .ExpectPreference("display.alwaysShow", "");
        Assert.Equal(2, s.Snap.ChangeSets.Count);
        Assert.Empty(s.H.Preferences.Compiled().WatchedTerms);
        Assert.Equal(mind.Name, s.Response.Producer);
    }

    /// <summary>"File Atlas decisions without asking": a standing grant files at the Review threshold; revoking it restores the question.</summary>
    [Fact]
    public void AStandingGrantFilesWithoutAskingAndIsRevocable()
    {
        // Nothing files automatically in this session unless a grant says so. Each change is scripted where the
        // scenario reaches it, because the second one names the grant the first one made.
        var mind = new ScriptedMind().Always(_ => MindStep.Of(Say("Filing for Atlas is what you asked for."), "Done"));
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); cfg.Orchestrator.AutoRouteThreshold = 0.99; }, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note("Decision: the Atlas beta ships on October 14.")
            .Dismiss();
        var atlas = s.H.Registry.FindActive("atlas")!;
        Assert.Empty(ProjectNoteStore.ReadAll(atlas.RootPath).Notes);
        var waiting = Assert.Single(s.Snap.Inbox);                                            // moderately confident routing waits for the user
        Assert.True(waiting.HasSuggestions);
        Assert.Equal("atlas", waiting.Candidates[0].Slug);
        var routingBefore = s.Snap.Inbox.Count;

        mind.Step(Propose(Actions.UpdatePreference, "You asked Relay to file Atlas decisions on its own.",
                    [("key", "filing.grant"), ("value", Actions.RouteNote), ("action", Actions.RouteNote), ("projectId", atlas.Id), ("noteType", NoteTypes.Decision),
                     .. Contract("Atlas decisions stop waiting in Review; fewer approvals for a filing that repeats",
                         $"Standing approval for {Actions.RouteNote} into atlas ({NoteTypes.Decision} notes); additive writes only",
                         "One standing grant recorded in preferences; revocable in one step",
                         "A decision routed to atlas with moderate confidence is filed and the ledger names the grant")]),
            "Proposing a standing grant for Atlas decisions", Read(0.3));

        s.Command("file Atlas decisions without asking")
            .ExpectProposal(Actions.UpdatePreference, "pending");
        var p = Assert.Single(s.Response.Proposals);
        Assert.Equal("filing.grant", p.Target["key"]);
        Assert.Equal(Actions.RouteNote, p.Target["value"]);
        Assert.Equal(NoteTypes.Decision, p.Target["noteType"]);
        Assert.Equal(atlas.Id, p.Target["projectId"]);
        s.Approve().ExpectState(RelayState.Ready).ExpectEvent(EventTypes.GrantApplied).Dismiss();
        var grant = Assert.Single(s.H.Preferences.Compiled().Grants);
        Assert.Equal(NoteTypes.Decision, grant.NoteType);
        s.ExpectPreference("filing.grant", $"{Actions.RouteNote}:{grant.ProjectId}/{NoteTypes.Decision}");

        // Same kind of note: filed without asking, and the ledger says which grant did it.
        s.Note("Decision: the Atlas launch review is on October 10.")
            .ExpectEvent(EventTypes.GrantApplied, 2)
            .Dismiss();
        var notes = ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note).ToList();
        var filed = Assert.Single(notes);
        Assert.Equal(NoteTypes.Decision, filed.Type);
        Assert.Contains("launch review", filed.Body);
        var applied = s.H.Last(EventTypes.GrantApplied)!;
        Assert.Equal(grant.GrantId, applied.DataString("grantId"));
        Assert.Equal(filed.Id, applied.DataString("noteId"));
        Assert.Equal(routingBefore, s.Snap.Inbox.Count);                                     // nothing new waits in the inbox

        // A different note type is not covered: it still waits.
        s.Note("Idea: an Atlas onboarding video with subtitles for new customers.").Dismiss();
        Assert.Equal(routingBefore + 1, s.Snap.Inbox.Count);
        Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes);

        // Revoke: the grant is removed by an approved change set and decisions wait again.
        mind.Step(Propose(Actions.UpdatePreference, "You asked Relay to stop filing Atlas decisions on its own.",
                    [("key", "filing.revoke"), ("value", grant.GrantId), ("projectId", atlas.Id),
                     .. Contract("Atlas decisions wait for your decision again",
                         "Writes config\\preferences.json (change set); removes a standing grant, adds none",
                         $"Removes grant {grant.GrantId}",
                         "The next decision routed to atlas appears in Review instead of being filed")]),
            "Proposing to revoke the standing grant", Read(0.3));

        s.Command("stop filing Atlas decisions without asking")
            .ExpectProposal(Actions.UpdatePreference, "pending");
        Assert.Equal("filing.revoke", Assert.Single(s.Response.Proposals).Target["key"]);
        Assert.Equal(grant.GrantId, Assert.Single(s.Response.Proposals).Target["value"]);
        s.Approve().ExpectState(RelayState.Ready).ExpectPreference("filing.grant", "").Dismiss()
            .Note("Decision: Atlas pricing goes public next spring with three tiers.").Dismiss();
        Assert.Equal(routingBefore + 2, s.Snap.Inbox.Count);
        Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes);
        Assert.Empty(s.H.Preferences.Compiled().Grants);
    }

    public void Dispose() => _tmp.Dispose();
}
