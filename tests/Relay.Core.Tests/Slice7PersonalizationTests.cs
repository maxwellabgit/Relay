using Relay.Core.Tests.Support;
using Relay.Core.Usage;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 7: friction evidence → typed improvement proposals with full contract and kind discrimination.
/// </summary>
public class Slice7PersonalizationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();

    [Fact]
    public void Below_threshold_yields_no_change()
    {
        _tmp.Root.EnsureLayout(new FixedClock(T0));
        var store = new FrictionEvidenceStore(_tmp.Root);
        store.Capture(FrictionKinds.FailedCapability, T0, "need:clock", "missing world clock", "case-a", ["ex-a"]);
        store.Capture(FrictionKinds.FailedCapability, T0.AddMinutes(1), "need:clock", "missing world clock", "case-b", ["ex-b"]);

        var proposal = store.Suggest(T0.AddHours(1));
        Assert.Equal(ImprovementKinds.NoChange, proposal.Kind);
        Assert.Empty(proposal.Validate());
    }

    [Fact]
    public void Repeated_failed_capability_proposes_tool_with_full_contract()
    {
        _tmp.Root.EnsureLayout(new FixedClock(T0));
        var store = new FrictionEvidenceStore(_tmp.Root);
        for (var i = 0; i < 3; i++)
        {
            store.Capture(
                FrictionKinds.FailedCapability,
                T0.AddMinutes(i),
                "need:world_clock",
                "capability gap",
                "case-" + i,
                ["ask-" + i]);
        }

        var proposal = store.Suggest(T0.AddHours(1));
        Assert.Equal(ImprovementKinds.Tool, proposal.Kind);
        Assert.Equal(FrictionKinds.FailedCapability, proposal.FrictionKind);
        Assert.Empty(proposal.Validate());
        Assert.NotEmpty(proposal.Examples);
        Assert.NotEmpty(proposal.ExpectedBenefit);
        Assert.NotEmpty(proposal.Inputs);
        Assert.NotEmpty(proposal.Outputs);
        Assert.NotEmpty(proposal.Permissions);
        Assert.NotEmpty(proposal.EvaluationCases);
        Assert.NotEmpty(proposal.ActivationScope);
        Assert.NotEmpty(proposal.ReversionPlan);
        Assert.NotEmpty(proposal.SuccessMetric);

        var loaded = store.LoadProposal(proposal.ProposalId);
        Assert.NotNull(loaded);
        Assert.Equal(proposal.Kind, loaded!.Kind);
    }

    [Fact]
    public void Each_friction_kind_maps_to_distinct_improvement_kind()
    {
        _tmp.Root.EnsureLayout(new FixedClock(T0));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FrictionKinds.RepeatedCorrection] = ImprovementKinds.Memory,
            [FrictionKinds.RepeatedToolSequence] = ImprovementKinds.Workflow,
            [FrictionKinds.FailedCapability] = ImprovementKinds.Tool,
            [FrictionKinds.RepeatedFiling] = ImprovementKinds.Preference,
            [FrictionKinds.ExcessiveIntervention] = ImprovementKinds.HostCapability,
        };

        foreach (var (friction, kind) in expected)
        {
            var root = new TempDataRoot();
            try
            {
                root.Root.EnsureLayout(new FixedClock(T0));
                var store = new FrictionEvidenceStore(root.Root);
                for (var i = 0; i < 3; i++)
                    store.Capture(friction, T0.AddMinutes(i), friction, "detail", "c" + i, ["e" + i]);
                var proposal = store.Suggest(T0.AddHours(1));
                Assert.Equal(kind, proposal.Kind);
                Assert.Empty(proposal.Validate());
            }
            finally { root.Dispose(); }
        }
    }

    [Fact]
    public void Incomplete_proposal_fails_validation()
    {
        var incomplete = new ImprovementProposal
        {
            ProposalId = "x",
            Kind = ImprovementKinds.Preference,
            Title = "t",
            ExpectedBenefit = "",
            ActivationScope = "",
            ReversionPlan = "",
            SuccessMetric = "",
        };
        var problems = incomplete.Validate();
        Assert.Contains(problems, p => p.Contains("examples", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("expectedBenefit", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("evaluationCases", StringComparison.Ordinal));
    }

    public void Dispose() => _tmp.Dispose();
}
