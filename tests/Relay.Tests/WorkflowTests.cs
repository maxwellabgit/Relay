using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.SelfChange;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Core.Workflows;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>Alpha Step 4: draft → dry-run → promote with one approval → list to mind → run → revert.</summary>
public class WorkflowTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly FixedClock _clock = new(Harness.T0);

    public void Dispose() => _tmp.Dispose();

    private static void MindMode(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    private static WorkflowDefinition Sample(string name = "list_and_say") => new()
    {
        Name = name,
        Description = "List projects then say how many there are.",
        Version = 1,
        Steps =
        [
            new WorkflowStep("use_tool", new Dictionary<string, string> { ["name"] = "list_projects" }),
            new WorkflowStep("say", new Dictionary<string, string> { ["text"] = "Here are your projects." }),
        ],
        DraftedAt = Harness.T0,
        BuiltBy = "test",
    };

    [Fact]
    public void PromotionNeedsATestedDraftIsOneChangeSetAndRevertingRemovesTheWorkflow()
    {
        var changes = new ChangeSetStore(_tmp.Root);
        var store = new WorkflowStore(_tmp.Root, changes);
        var builder = new WorkflowBuilder(store, () => _clock.UtcNow);

        var untested = Sample();
        store.SaveDraft(untested);
        Assert.Contains("has not passed its dry-run", store.Promote("list_and_say", "why", _clock.UtcNow, null, null).Error);
        Assert.Contains("no draft workflow named 'other'", store.Promote("other", "why", _clock.UtcNow, null, null).Error);

        var report = builder.Test(untested);
        Assert.True(report.Passed, report.Summary);
        Assert.True(store.Draft("list_and_say")!.Tested);

        var promoted = store.Promote("list_and_say", "Built for listing projects", _clock.UtcNow, "task-1", "prop-1");
        Assert.True(promoted.Ok, promoted.Error);
        Assert.Equal(ChangeKinds.Workflow, promoted.ChangeSet!.Kind);
        Assert.Null(promoted.ChangeSet.Before);
        Assert.True(store.IsPromoted("list_and_say"));
        Assert.Null(store.Draft("list_and_say"));
        Assert.Contains(store.Descriptors(), d => d.Name == "list_and_say" && d.StepKinds.SequenceEqual(["use_tool", "say"]));
        Assert.NotNull(store.Promoted("list_and_say")!.PromotedAt);

        store.SaveDraft(report.Package);
        Assert.Contains("already exists", store.Promote("list_and_say", "again", _clock.UtcNow, null, null).Error ?? "");

        var reverted = changes.Revert(promoted.ChangeSet.ChangeSetId, "not wanted", _clock.UtcNow);
        Assert.True(reverted.Ok, reverted.Error);
        Assert.False(store.IsPromoted("list_and_say"));
        Assert.Empty(store.Descriptors());
        Assert.False(File.Exists(store.PromotedPath("list_and_say")));
    }

    [Fact]
    public void ValidateRejectsUntestedAndWrongSha()
    {
        using var h = new Harness(_tmp.Root, clock: _clock).Start();
        var store = h.Workflows.Store;
        var draft = Sample();
        store.SaveDraft(draft);

        Decision Decide(params (string Key, string Value)[] target)
            => PolicyEngine.Decide(
                new Proposal("01PROPOSAL0000000000000000", Actions.AddWorkflow, "why",
                    target.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal),
                    ["01SOURCE000000000000000000"], [], Risks.ControlledWrite, true, Producers.Mind),
                new PolicyWorld
                {
                    Registry = h.Registry, Roots = h.Roots, DataRoot = h.Root,
                    DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false,
                    Origin = TaskOrigin.Direct, Kind = TaskKind.Improve,
                });

        var untested = Decide(("name", "list_and_say"), ("definitionSha256", "abc"),
            ("benefit", "b"), ("permissions", "p"), ("scope", "s"), ("acceptance", "a"));
        Assert.Equal(DecisionOutcome.Deny, untested.Outcome);
        Assert.Contains(untested.Reasons, r => r.Contains("has not passed its dry-run", StringComparison.Ordinal));

        var report = h.Workflows.Builder.Test(draft);
        Assert.True(report.Passed, report.Summary);

        var wrongSha = Decide(("name", "list_and_say"), ("definitionSha256", "deadbeef"),
            ("benefit", "b"), ("permissions", "p"), ("scope", "s"), ("acceptance", "a"));
        Assert.Equal(DecisionOutcome.Deny, wrongSha.Outcome);
        Assert.Contains(wrongSha.Reasons, r => r.Contains("definition changed", StringComparison.Ordinal));

        var ok = Decide(("name", "list_and_say"), ("definitionSha256", report.Package.DefinitionSha256),
            ("benefit", "Lists projects in one step."), ("permissions", "Runs read-only tools only."),
            ("scope", "Adds workflow list_and_say."), ("acceptance", report.Summary));
        Assert.Equal(DecisionOutcome.NeedsApproval, ok.Outcome);
    }

    [Fact]
    public void TheMindPromotesAWorkflowWithOneApprovalListsItRunsItAndRevertRemovesIt()
    {
        var draft = Sample();
        var storePath = Path.Combine(_tmp.Root.WorkflowDraftsDirectory, "list_and_say.json");
        Directory.CreateDirectory(_tmp.Root.WorkflowDraftsDirectory);

        var mind = new ScriptedMind().Always(req =>
        {
            if (req.Transcript[^1] is InputObserved)
            {
                var store = new WorkflowStore(_tmp.Root, new ChangeSetStore(_tmp.Root));
                var report = new WorkflowBuilder(store, () => _clock.UtcNow).Test(draft, "task");
                Assert.True(report.Passed, report.Summary);
                return MindStep.Of(Propose(Actions.AddWorkflow, "Built for this task",
                    ("name", "list_and_say"),
                    ("definitionSha256", report.Package.DefinitionSha256),
                    ("benefit", "Lists projects then says so."),
                    ("permissions", "Runs read-only tools; no new authority."),
                    ("scope", "Adds workflow list_and_say (use_tool → say)."),
                    ("acceptance", report.Summary)), "Proposing the workflow");
            }
            if (req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Promoted)
                && !req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Finished))
            {
                Assert.Contains(req.Context.Workflows, w => w.Name == "list_and_say");
                return MindStep.Of(RunWorkflow("list_and_say"), "Running the new workflow");
            }
            if (req.Transcript.OfType<WorkflowObserved>().Any(w => w.Stage == WorkflowObserved.Finished))
                return MindStep.Of(Say("Listed your projects via the workflow."), "Done");
            if (req.Transcript.OfType<ApprovalObserved>().Any(a => !a.Granted))
                return MindStep.Of(Say("Kept the draft in staging."), "Done");
            return MindStep.Of(Say("Unexpected state."), "Done");
        });

        using var s = Scenario.New(_tmp, MindMode, mind: mind, clock: _clock)
            .WithWorkspace()
            .Ask("Add a workflow that lists my projects")
            .ExpectProposal(Actions.AddWorkflow, "pending")
            .ExpectNoEvent(EventTypes.WorkflowPromoted);

        Assert.True(File.Exists(storePath));

        s.Approve(Actions.AddWorkflow)
            .PumpUntil("the workflow to run and the answer", () => s.ForegroundSettled, TimeSpan.FromSeconds(10))
            .ExpectState(RelayState.Ready)
            .ExpectProposal(Actions.AddWorkflow, "executed")
            .ExpectEvent(EventTypes.WorkflowPromoted)
            .ExpectEvent(EventTypes.WorkflowRan);

        Assert.True(File.Exists(Path.Combine(_tmp.Root.WorkflowsDirectory, "list_and_say.json")));
        Assert.False(File.Exists(storePath));
        Assert.Contains(s.H.Workflows.Descriptors(), d => d.Name == "list_and_say");
        Assert.Contains("Listed your projects via the workflow", s.Response.Answer ?? "");

        var changeSet = s.H.ChangeSets.All().Single(c => c.Kind == ChangeKinds.Workflow && !c.Reverted);
        var reverted = s.H.ChangeSets.Revert(changeSet.ChangeSetId, "not wanted", _clock.UtcNow);
        Assert.True(reverted.Ok, reverted.Error);
        Assert.False(s.H.Workflows.Store.IsPromoted("list_and_say"));
        Assert.False(File.Exists(Path.Combine(_tmp.Root.WorkflowsDirectory, "list_and_say.json")));
    }

    [Fact]
    public void MindPromptListsPromotedWorkflows()
    {
        var context = new MindContext
        {
            Workflows = [new WorkflowDescriptor("list_and_say", "List then say", 1, ["use_tool", "say"])],
        };
        var system = MindPrompt.System(context);
        Assert.Contains("Workflows available:", system);
        Assert.Contains("list_and_say v1", system);
        Assert.Contains("run_workflow", system);
    }
}
