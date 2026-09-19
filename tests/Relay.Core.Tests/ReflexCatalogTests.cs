using Relay.Core.Connectors;
using Relay.Core.Reflexes;

namespace Relay.Core.Tests;

public sealed class ReflexCatalogTests
{
    [Fact]
    public void Alpha_registers_exactly_four_versioned_reflexes()
    {
        Assert.Equal(4, ReflexCatalog.AlphaReflexes.Count);
        Assert.Equal(
            ReflexCatalog.AlphaReflexes.Count,
            ReflexCatalog.AlphaReflexes.Select(r => r.Ref.Display).Distinct(StringComparer.Ordinal).Count());
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
        Assert.NotEmpty(reflex.Judgments);
        Assert.NotEmpty(reflex.EvaluationFixtureIds);
        Assert.False(string.IsNullOrWhiteSpace(reflex.ExplanationTemplate));
        Assert.True(reflex.Budgets.MaxSourceAttempts > 0);
        Assert.Equal(ReflexActivationState.Inactive, reflex.DefaultActivation);
        Assert.NotNull(reflex.Rollback);
    }

    [Fact]
    public void Remember_birthday_has_compensating_rollback_action()
    {
        var reflex = ReflexCatalog.Require("reflex.remember-birthday");
        Assert.Equal(RollbackStrategy.CompensatingAction, reflex.Rollback.Strategy);
        Assert.NotNull(reflex.Rollback.CompensatingAction);
        Assert.Equal(
            "google-calendar.event-create@1",
            Assert.Single(reflex.PermittedWriteActions).Display.Split('/')[^1]);
    }

    [Fact]
    public void Verify_technical_claim_has_no_writes_and_search_reads()
    {
        var reflex = ReflexCatalog.Require("reflex.verify-technical-claim");
        Assert.Empty(reflex.PermittedWriteActions);
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "conversation.search");
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "gmail.messages-search");
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "github.code-search");
        Assert.Equal(RollbackStrategy.None, reflex.Rollback.Strategy);
    }

    [Fact]
    public void Preserve_important_information_allows_observed_sources()
    {
        var reflex = ReflexCatalog.Require("reflex.preserve-important-information");
        Assert.Contains(reflex.Triggers, t => t == "observed_source_item");
        Assert.Contains(reflex.PermittedSources, s => s.Id == "gmail");
        Assert.Contains(reflex.PermittedSources, s => s.Id == "github");
        Assert.Equal(ApprovalMode.StandingGrantEligible, reflex.ApprovalMode);
    }

    [Fact]
    public void Resolve_acronym_searches_conversation_gmail_and_github()
    {
        var reflex = ReflexCatalog.Require("reflex.resolve-acronym");
        Assert.Equal("memory.search", reflex.ReadPlan[0].ActionId);
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "conversation.search");
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "gmail.messages-search");
        Assert.Contains(reflex.ReadPlan, a => a.ActionId == "github.issues-search");
        Assert.Equal(RollbackStrategy.CompensatingAction, reflex.Rollback.Strategy);
    }
}
