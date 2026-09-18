using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class AcronymResolveTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Acronym_exact_project_BESS_resolves_with_zero_Jev_calls()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, _) = local.SeedLightshiftBess();
        var fake = new FakeJudgmentClient();
        var capability = new AcronymResolveCapability(
            new GlossaryStore(_tmp.Root),
            id => local.Registry.ById(id)?.RootPath,
            client: fake);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "case-bess",
            Origin = CaseOrigin.Direct,
            ProjectId = project.Id,
            Objective = "What does BESS mean?",
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "What does BESS mean in Lightshift?",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(AcronymResolveKinds.Resolved, result.Kind);
        Assert.Equal("BESS — Battery Energy Storage System", result.FeedText);
        Assert.Equal(0, fake.CallCount);
        Assert.Equal("deterministic_glossary", result.Reason);
    }

    [Fact]
    public async Task Acronym_project_and_global_conflict_Jev_selects_project_context_match()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, projectEntry) = local.SeedLightshiftBess("Battery Energy Storage System");
        var store = new GlossaryStore(_tmp.Root);
        store.SaveGlobal(
        [
            new GlossaryEntry
            {
                Id = "bess-global",
                Acronym = "BESS",
                Expansion = "Building Energy Supervisory System",
                Scope = GlossaryScopes.Global,
            },
        ]);

        var projectCandidateId = $"project:{projectEntry.Id}";
        var fake = new FakeJudgmentClient().Script(
            QuestionSets.AcronymSelectId,
            JudgmentResponse.FromSuccess(new JudgmentSuccess
            {
                Model = "jev-1.13.0",
                Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
                {
                    ["select"] = new ChoiceAnswer
                    {
                        Choice = projectCandidateId,
                        Confidence = 0.82,
                        Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            [projectCandidateId] = 0.75,
                            ["global:bess-global"] = 0.15,
                            [ChoiceQuestion.NoMatch] = 0.10,
                        },
                    },
                },
            }));

        var capability = new AcronymResolveCapability(
            store,
            id => local.Registry.ById(id)?.RootPath,
            client: fake);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "case-conflict",
            Origin = CaseOrigin.Observed,
            ProjectId = project.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "In Lightshift, clarify BESS before the board meeting.",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(AcronymResolveKinds.Resolved, result.Kind);
        Assert.Equal("BESS — Battery Energy Storage System", result.FeedText);
        Assert.Equal(1, fake.CallCount);
        Assert.DoesNotContain("Building Energy", result.FeedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acronym_no_match_publishes_unresolved_feed_without_invented_expansion()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, projectEntry) = local.SeedLightshiftBess("Battery Energy Storage System");
        var store = new GlossaryStore(_tmp.Root);
        store.SaveGlobal(
        [
            new GlossaryEntry
            {
                Id = "bess-global",
                Acronym = "BESS",
                Expansion = "Building Energy Supervisory System",
                Scope = GlossaryScopes.Global,
            },
        ]);

        var projectCandidateId = $"project:{projectEntry.Id}";
        var fake = new FakeJudgmentClient().Script(
            QuestionSets.AcronymSelectId,
            JudgmentResponse.FromSuccess(new JudgmentSuccess
            {
                Model = "jev-1.13.0",
                Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
                {
                    ["select"] = new ChoiceAnswer
                    {
                        Choice = ChoiceQuestion.NoMatch,
                        Confidence = 0.9,
                        Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            [projectCandidateId] = 0.2,
                            ["global:bess-global"] = 0.2,
                            [ChoiceQuestion.NoMatch] = 0.6,
                        },
                    },
                },
            }));

        var capability = new AcronymResolveCapability(
            store,
            id => local.Registry.ById(id)?.RootPath,
            client: fake);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "case-nomatch",
            Origin = CaseOrigin.Direct,
            ProjectId = project.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = "BESS",
                ["span"] = "What is BESS in this unrelated context?",
            },
            At = _clock.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(AcronymResolveKinds.Unresolved, result.Kind);
        Assert.Equal("Unresolved: BESS", result.FeedText);
        Assert.Equal("no_match", result.Reason);
        Assert.DoesNotContain("Battery", result.FeedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Building", result.FeedText, StringComparison.Ordinal);
        Assert.Equal(1, fake.CallCount);
    }

    public void Dispose() => _tmp.Dispose();
}
