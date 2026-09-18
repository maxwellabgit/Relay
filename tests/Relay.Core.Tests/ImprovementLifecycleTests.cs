using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Memory;
using Relay.Core.Projects;
using Relay.Core.Tests.Support;
using Relay.Core.Usage;

namespace Relay.Core.Tests;

public sealed class ImprovementLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 16, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task BESS_glossary_improvement_lifecycle_nine_steps()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (lightshift, _) = local.SeedLightshiftBess(expansion: "PLACEHOLDER");
        // Start without a useful entry — clear and leave empty for friction path.
        new GlossaryStore(_tmp.Root).SaveProject(lightshift.RootPath, []);

        var other = new ProjectRecord
        {
            Id = UlidLike("other"),
            Slug = "otherco",
            Name = "OtherCo",
            RootPath = Path.Combine(local.ProjectsHome, "otherco"),
            CreatedAt = _clock.UtcNow,
        };
        Directory.CreateDirectory(other.RootPath);
        Directory.CreateDirectory(Path.Combine(other.RootPath, ".orchestrator"));

        var life = new GlossaryImprovementLifecycle(_tmp.Root);
        var signature = PatternSignature.ForAcronymCorrection(lightshift.Id, "BESS");

        // Unrelated corrections must not join.
        var otherSig = PatternSignature.ForAcronymCorrection(other.Id, "API");
        life.Friction.Capture(
            FrictionKinds.RepeatedCorrection,
            _clock.UtcNow,
            otherSig.Key,
            "Application Programming Interface",
            signature: otherSig);

        // Steps 1–3: three Lightshift BESS corrections → one proposal
        var proposal = life.DraftFromSignature(
            signature,
            "Battery Energy Storage System",
            _clock.UtcNow,
            corrections: 3);
        Assert.Equal(ImprovementKinds.Memory, proposal.Kind);
        Assert.Contains("BESS", proposal.Title, StringComparison.Ordinal);
        Assert.Equal(ImprovementStatuses.Draft, proposal.Status);
        Assert.DoesNotContain(proposal.EvidenceIds, id => id.Length == 0);

        // Step 4: unrelated API correction did not merge into BESS proposal
        Assert.All(proposal.Examples, ex => Assert.DoesNotContain("API", ex, StringComparison.Ordinal));

        // Steps 5: evaluation
        var (passed, evaluated, results) = life.Evaluate(proposal.ProposalId, lightshift.RootPath, other.RootPath);
        Assert.True(passed, string.Join("; ", results.Select(r => $"{r.FixtureName}={r.Passed}:{r.Detail}")));
        Assert.Equal(ImprovementStatuses.ReadyForApproval, evaluated.Status);
        var evalHash = life.LoadLifecycle(proposal.ProposalId)!.EvalArtifactHash!;

        // Restart durability after evaluate
        var life2 = new GlossaryImprovementLifecycle(_tmp.Root);
        Assert.Equal(ImprovementStatuses.ReadyForApproval, life2.LoadLifecycle(proposal.ProposalId)!.Status);

        // Steps 6–7: approve exact change-set hash + shadow
        var changeHash = life2.Approve(proposal.ProposalId, evalHash, lightshift.RootPath, _clock.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(changeHash));
        Assert.Equal(ImprovementStatuses.ActiveShadow, life2.LoadLifecycle(proposal.ProposalId)!.Status);

        // Step 7 continued: Lightshift BESS resolves with zero Jev
        var fake = new Support.FakeJudgmentClient();
        var resolve = new AcronymResolveCapability(
            life2.Glossary,
            id => id == lightshift.Id ? lightshift.RootPath : other.RootPath,
            client: fake);
        var hit = await resolve.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "post-activate",
            Origin = CaseOrigin.Direct,
            ProjectId = lightshift.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "Clarify BESS for the board.",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);
        Assert.Equal(AcronymResolveKinds.Resolved, hit.Kind);
        Assert.Equal("BESS — Battery Energy Storage System", hit.FeedText);
        Assert.Equal(0, fake.CallCount);

        // Step 8: other project does not inherit
        var miss = await resolve.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "other-proj",
            Origin = CaseOrigin.Direct,
            ProjectId = other.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "What is BESS?",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);
        Assert.NotEqual(AcronymResolveKinds.Resolved, miss.Kind);

        life2.RecordShadowUse(proposal.ProposalId);
        life2.RecordShadowUse(proposal.ProposalId);
        life2.RecordShadowUse(proposal.ProposalId);
        Assert.Equal(ImprovementStatuses.Active, life2.LoadLifecycle(proposal.ProposalId)!.Status);

        // Step 9: revert restores prior behavior
        life2.Revert(proposal.ProposalId, lightshift.RootPath, _clock.UtcNow);
        Assert.Equal(ImprovementStatuses.Reverted, life2.LoadLifecycle(proposal.ProposalId)!.Status);
        var afterRevert = await resolve.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "after-revert",
            Origin = CaseOrigin.Direct,
            ProjectId = lightshift.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "What is BESS?",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);
        Assert.NotEqual(AcronymResolveKinds.Resolved, afterRevert.Kind);
    }

    [Fact]
    public void Suggest_groups_by_pattern_signature_not_kind_alone()
    {
        _tmp.Root.EnsureLayout(_clock);
        var store = new FrictionEvidenceStore(_tmp.Root);
        var a = PatternSignature.ForAcronymCorrection("proj-a", "BESS");
        var b = PatternSignature.ForAcronymCorrection("proj-b", "BESS");
        for (var i = 0; i < 3; i++)
        {
            store.Capture(FrictionKinds.RepeatedCorrection, _clock.UtcNow.AddMinutes(i), a.Key, "x", signature: a);
            store.Capture(FrictionKinds.RepeatedCorrection, _clock.UtcNow.AddMinutes(i), b.Key, "y", signature: b);
        }

        var proposal = store.Suggest(_clock.UtcNow);
        Assert.Equal(ImprovementKinds.Memory, proposal.Kind);
        // One proposal for one signature (top score); both have score 3 — either project, but not merged evidence from both randomly mixed without signature.
        Assert.True(proposal.EvidenceIds.Count >= 3);
        Assert.True(proposal.EvidenceIds.Count <= 3);
    }

    private static string UlidLike(string seed) => "01TEST" + seed.PadRight(20, '0')[..20];

    public void Dispose() => _tmp.Dispose();
}
