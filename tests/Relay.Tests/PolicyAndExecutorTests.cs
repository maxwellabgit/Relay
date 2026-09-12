using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Policy;
using Relay.Core.Tasks;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>
/// The deterministic authority, on its own: the tier of an action is fixed by the action and nothing
/// else, a proposal's hash covers exactly what the user approved, a capability is single-use and bound
/// to that hash, and the executor re-checks policy against the world as it is when it runs rather than
/// as it was when the card was shown. Whoever proposed — the mind, the user's own click — meets the
/// same gates, so these tests address the engine directly rather than through a task.
/// </summary>
public class PolicyAndExecutorTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Theory]
    [InlineData(Actions.CreateDraftNote, Tier.Automatic)]
    [InlineData(Actions.RouteNote, Tier.Automatic)]
    [InlineData(Actions.CreateProject, Tier.RequiresApproval)]
    [InlineData(Actions.ModifyNote, Tier.RequiresApproval)]
    [InlineData(Actions.ArchiveProject, Tier.RequiresApproval)]
    [InlineData(Actions.LaunchWorker, Tier.RequiresApproval)]
    [InlineData(Actions.DeleteProject, Tier.RequiresApproval)]
    [InlineData(Actions.ModelRequest, Tier.RequiresApproval)]
    [InlineData(Actions.UpdatePreference, Tier.RequiresApproval)]
    [InlineData(Actions.RunShell, Tier.Prohibited)]
    [InlineData("format_disk", Tier.Prohibited)]
    public void TiersAreFixedByAction(string action, Tier tier) => Assert.Equal(tier, PolicyEngine.TierOf(action));

    [Fact]
    public void EveryProposalMustCiteASourceAndSomeActionsOnlyADirectRequest()
    {
        using var h = new Harness(_tmp.Root).Start();
        PolicyWorld World(TaskOrigin? origin = null) => new()
        {
            Registry = h.Registry, Roots = h.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false, Origin = origin,
        };
        Proposal Delete(params string[] sources)
            => new("1", Actions.DeleteProject, "r", new Dictionary<string, string> { ["projectId"] = "01J", ["confirm"] = "delete" }, sources, [], Risks.ControlledWrite, true, Producers.Mind);

        // No source event: the proposal cannot be traced back to an instruction or a capture, so it is refused before its target is looked at.
        var unsourced = PolicyEngine.Decide(Delete(), World());
        Assert.Equal(DecisionOutcome.Deny, unsourced.Outcome);
        Assert.Contains(unsourced.Reasons, r => r.Contains("no source event", StringComparison.Ordinal));
        Assert.Empty(unsourced.NormalizedTarget); // the target was never normalized

        // Overheard: a destructive action needs the user's own words, whatever the conversation seemed to ask for.
        foreach (var origin in new[] { TaskOrigin.Observed, TaskOrigin.Dialogue })
        {
            var overheard = PolicyEngine.Decide(Delete("e1"), World(origin));
            Assert.Equal(DecisionOutcome.Deny, overheard.Outcome);
            Assert.Contains(overheard.Reasons, r => r.Contains("only be proposed from a direct request", StringComparison.Ordinal));
        }
        Assert.All(Actions.DirectOnly, action => Assert.Equal(DecisionOutcome.Deny, PolicyEngine.Decide(Delete("e1") with { Action = action }, World(TaskOrigin.Observed)).Outcome));
    }

    [Fact]
    public void AProposerClaimingNoApprovalIsNeededIsOverruledAndTheClaimIsShown()
    {
        using var h = new Harness(_tmp.Root).Start();
        var ws = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-ws1");
        Directory.CreateDirectory(ws);
        try
        {
            h.Coordinator.RegisterWorkspace(ws);
            var world = new PolicyWorld { Registry = h.Registry, Roots = h.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false };
            var claimed = new Proposal("1", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "Atlas" }, ["e1"], [], Risks.ControlledWrite, false, Producers.Mind);

            var decision = PolicyEngine.Decide(claimed, world);
            Assert.Equal(DecisionOutcome.NeedsApproval, decision.Outcome);
            Assert.Contains(decision.Reasons, r => r.Contains("claimed no approval was needed", StringComparison.Ordinal));
            // The user's own click carries the approval with it, so the same flag from the user is not a claim to overrule.
            Assert.DoesNotContain(PolicyEngine.Decide(claimed with { ProposedBy = Producers.User }, world).Reasons, r => r.Contains("claimed no approval", StringComparison.Ordinal));
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [Fact]
    public void ProposalHashCoversActionTargetAndSourcesOnly()
    {
        var a = new Proposal("1", Actions.CreateProject, "r1", new Dictionary<string, string> { ["name"] = "X", ["slug"] = "x" }, ["e1"], ["eff"], Risks.ControlledWrite, true, Producers.Mind);
        var b = a with { ProposalId = "2", Reason = "different", ExpectedEffects = [] };
        var c = a with { Target = new Dictionary<string, string> { ["slug"] = "x", ["name"] = "X" } };
        var d = a with { Target = new Dictionary<string, string> { ["name"] = "Y", ["slug"] = "x" } };
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), c.Hash());
        Assert.NotEqual(a.Hash(), d.Hash());
    }

    [Fact]
    public void CapabilitiesAreSingleUseAndBoundToTheProposalHash()
    {
        var issuer = new CapabilityIssuer();
        var p = new Proposal("1", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "X" }, ["e1"], [], Risks.ControlledWrite, true, Producers.Mind);
        var cap = issuer.Issue(p, Harness.T0);
        Assert.True(issuer.Consume(cap, p, Harness.T0).Ok);
        Assert.Contains("already used", issuer.Consume(cap, p, Harness.T0).Reason);

        var cap2 = issuer.Issue(p, Harness.T0);
        var changed = p with { Target = new Dictionary<string, string> { ["name"] = "Y" } };
        Assert.Contains("changed after approval", issuer.Consume(cap2, changed, Harness.T0).Reason);
        Assert.Contains("expired", issuer.Consume(cap2, p, Harness.T0 + TimeSpan.FromHours(1)).Reason);
        var forged = cap2 with { Signature = new string('0', 64) };
        Assert.Contains("signature", issuer.Consume(forged, p, Harness.T0).Reason);
        Assert.False(new CapabilityIssuer().Consume(cap2, p, Harness.T0).Ok); // another session's key
    }

    [Fact]
    public void ExecutorRechecksPolicyAtExecutionTime()
    {
        using var h = new Harness(_tmp.Root).Start();
        var ws = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-ws2");
        Directory.CreateDirectory(ws);
        try
        {
            h.Coordinator.RegisterWorkspace(ws);
            var issuer = new CapabilityIssuer();
            var executor = new Executor(_tmp.Root, h.Registry, h.Roots, h.Notes, h.Clock, issuer);
            var world = new PolicyWorld { Registry = h.Registry, Roots = h.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false };
            var p = new Proposal("1", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "Atlas" }, ["e1"], [], Risks.ControlledWrite, true, Producers.Mind);
            Assert.Equal(DecisionOutcome.NeedsApproval, PolicyEngine.Decide(p, world).Outcome);
            var cap = issuer.Issue(p, h.Clock.UtcNow);

            // The world changes between approval and execution: the target folder appears with content.
            Directory.CreateDirectory(Path.Combine(ws, "atlas"));
            File.WriteAllText(Path.Combine(ws, "atlas", "stray.txt"), "x");

            var sink = new RecordingSink();
            var result = executor.Execute(p, cap, world, "T", sink);
            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains("Preconditions no longer hold", result.Error);
            Assert.Contains(sink.Events, e => e.Type == EventTypes.ExecutionFailed && e.Stage == "recheck");
            Assert.DoesNotContain(sink.Events, e => e.Type == EventTypes.ExecutionStarted);
            Assert.Null(h.Registry.FindActive("atlas"));
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    private sealed class RecordingSink : IExecutionSink
    {
        public List<(string Type, string? Stage)> Events { get; } = new();
        public LedgerRecord? Record(string type, object data)
        {
            var stage = data.GetType().GetProperty("stage")?.GetValue(data) as string;
            Events.Add((type, stage));
            return null;
        }
    }

    public void Dispose() => _tmp.Dispose();
}
