using Relay.Core.Reflexes;

namespace Relay.Core.Tests;

public sealed class ReflexCatalogTests
{
    [Fact]
    public void Alpha_registers_exactly_four_reflexes()
    {
        Assert.Equal(4, ReflexCatalog.AlphaReflexes.Count);
        Assert.Equal(
            ReflexCatalog.AlphaReflexes.Count,
            ReflexCatalog.AlphaReflexes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("reflex.remember-birthday")]
    [InlineData("reflex.verify-technical-claim")]
    [InlineData("reflex.preserve-important-information")]
    [InlineData("reflex.resolve-acronym")]
    public void Each_reflex_declares_required_policy_fields(string reflexId)
    {
        var reflex = ReflexCatalog.Require(reflexId);

        Assert.NotEmpty(reflex.Triggers);
        Assert.NotEmpty(reflex.NegativeTriggers);
        Assert.NotEmpty(reflex.Conditions);
        Assert.NotEmpty(reflex.PermittedSources);
        Assert.NotEmpty(reflex.ReadPlan);
        Assert.NotEmpty(reflex.JudgmentDefinitionIds);
        Assert.NotEmpty(reflex.EvaluationFixtureIds);
        Assert.False(string.IsNullOrWhiteSpace(reflex.ExplanationTemplate));
        Assert.True(reflex.Budgets.MaxSourceAttempts > 0);
        Assert.True(reflex.Budgets.MaxJudgmentRounds > 0);
        Assert.True(reflex.Budgets.MaxHostedTokens > 0);
        Assert.True(reflex.RetryPolicy.MaxAttempts > 0);
        Assert.Equal(ReflexActivationState.Inactive, reflex.DefaultActivation);
    }

    [Fact]
    public void Remember_birthday_permits_only_calendar_create()
    {
        var reflex = ReflexCatalog.Require("reflex.remember-birthday");
        Assert.Equal(["google-calendar.event-create"], reflex.PermittedWriteActionIds);
        Assert.Contains("google-calendar", reflex.PermittedSources);
        Assert.Contains("birthday-statement@1", reflex.JudgmentDefinitionIds);
    }

    [Fact]
    public void Verify_technical_claim_has_no_writes_and_bounded_budgets()
    {
        var reflex = ReflexCatalog.Require("reflex.verify-technical-claim");
        Assert.Empty(reflex.PermittedWriteActionIds);
        Assert.Equal(3, reflex.Budgets.MaxSourceAttempts);
        Assert.Equal(2, reflex.Budgets.MaxJudgmentRounds);
        Assert.Contains("claim-support@1", reflex.JudgmentDefinitionIds);
    }

    [Fact]
    public void Preserve_important_information_permits_local_note_create()
    {
        var reflex = ReflexCatalog.Require("reflex.preserve-important-information");
        Assert.Equal(["memory.note-create"], reflex.PermittedWriteActionIds);
        Assert.Equal(ApprovalMode.StandingGrantEligible, reflex.ApprovalMode);
        Assert.True(reflex.SupportsRollback);
    }

    [Fact]
    public void Resolve_acronym_permits_remember_but_lookup_is_read_first()
    {
        var reflex = ReflexCatalog.Require("reflex.resolve-acronym");
        Assert.Equal(["memory.acronym-remember"], reflex.PermittedWriteActionIds);
        Assert.Equal("memory.search", reflex.ReadPlan[0]);
        Assert.Contains("acronym-candidate-choice@1", reflex.JudgmentDefinitionIds);
    }
}
