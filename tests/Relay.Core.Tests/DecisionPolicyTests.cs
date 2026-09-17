using System.Text.Json;
using Relay.Core.Judgments;

namespace Relay.Core.Tests;

public class DecisionPolicyTests
{
    private static DecisionCatalog Catalog()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "decisions", "v1");
        return DecisionCatalog.LoadFromDirectory(root);
    }

    private static JudgmentResponse NoulResponse(string key, double noul) => new()
    {
        Model = "fixture",
        Answers = new Dictionary<string, JsonElement>
        {
            [key] = JsonSerializer.SerializeToElement(new { noul }),
        },
        Usage = new JudgmentUsage { InputTokens = 1 },
        Ok = true,
    };

    private static JudgmentResponse ChoiceResponse(string key, string choice, Dictionary<string, double> probs, double confidence = 0.9) => new()
    {
        Model = "fixture",
        Answers = new Dictionary<string, JsonElement>
        {
            [key] = JsonSerializer.SerializeToElement(new { choice, probabilities = probs, confidence }),
        },
        Usage = new JudgmentUsage { InputTokens = 1 },
        Ok = true,
    };

    [Fact]
    public void Catalog_loads_all_required_decision_ids()
    {
        var catalog = Catalog();
        string[] required =
        [
            "passage.correction", "passage.commitment", "passage.unresolved_question",
            "passage.recurring_friction", "passage.explicit_urgency",
            "candidate.case_relation", "candidate.evidence_relevance",
            "reference.resolution", "evidence.claim_relation", "statement.revision_relation",
            "context.target_identified", "context.required_fact", "capability.request_match",
            "job.reference_interpretation", "job.extended_reasoning",
            "output.extraction_support", "output.argument_support", "output.claim_support",
            "output.criterion_satisfied", "content.instruction_attempt",
        ];
        foreach (var id in required)
            Assert.True(catalog.TryGet(id) is not null, "missing " + id);
    }

    [Fact]
    public void One_passage_three_independent_findings()
    {
        var catalog = Catalog();
        var correction = catalog.TryGet("passage.correction")!;
        var commitment = catalog.TryGet("passage.commitment")!;
        var question = catalog.TryGet("passage.unresolved_question")!;

        var a = DecisionPolicy.Interpret(correction, NoulResponse("correction", 0.9));
        var b = DecisionPolicy.Interpret(commitment, NoulResponse("commitment", 0.15));
        var c = DecisionPolicy.Interpret(question, NoulResponse("unresolved_question", 0.85));

        Assert.Equal(DecisionPolarity.Positive, a.Polarity);
        Assert.Equal(DecisionPolarity.Negative, b.Polarity);
        Assert.Equal(DecisionPolarity.Positive, c.Polarity);
        Assert.True(DecisionPolicy.AnswersAreIndependent);
        // Each finding stands alone — no cross-consumption of answers.
        Assert.NotEqual(a.QuestionKey, b.QuestionKey);
        Assert.NotEqual(b.QuestionKey, c.QuestionKey);
    }

    [Fact]
    public void Ambiguous_capability_does_not_pick_arbitrarily()
    {
        var def = Catalog().TryGet("capability.request_match")!;
        var interp = DecisionPolicy.Interpret(def, ChoiceResponse("request_match", "candidates",
            new Dictionary<string, double> { ["candidates"] = 0.85, ["none"] = 0.10, ["unclear"] = 0.05 }));
        Assert.Null(DecisionPolicy.SelectCapability(interp, ["cap-a", "cap-b"]));

        var unclear = DecisionPolicy.Interpret(def, ChoiceResponse("request_match", "unclear",
            new Dictionary<string, double> { ["candidates"] = 0.4, ["none"] = 0.2, ["unclear"] = 0.4 }, confidence: 0.99));
        Assert.Null(DecisionPolicy.SelectCapability(unclear, ["cap-a"]));
    }

    [Fact]
    public void High_confidence_unsupported_claim_blocked()
    {
        var def = Catalog().TryGet("output.claim_support")!;
        var interp = DecisionPolicy.Interpret(def, ChoiceResponse("claim_support", "unsupported",
            new Dictionary<string, double>
            {
                ["supported"] = 0.05, ["unsupported"] = 0.85, ["insufficient"] = 0.05, ["unclear"] = 0.05,
            }, confidence: 0.99));
        Assert.False(DecisionPolicy.AllowClaim(interp));
        Assert.False(interp.GrantsPermission);
    }

    [Fact]
    public void One_failed_criterion_blocks_completion()
    {
        var def = Catalog().TryGet("output.criterion_satisfied")!;
        var ok = DecisionPolicy.Interpret(def, ChoiceResponse("criterion_satisfied", "satisfied",
            new Dictionary<string, double> { ["satisfied"] = 0.9, ["unsatisfied"] = 0.05, ["unclear"] = 0.05 }));
        var bad = DecisionPolicy.Interpret(def, ChoiceResponse("criterion_satisfied", "unsatisfied",
            new Dictionary<string, double> { ["satisfied"] = 0.1, ["unsatisfied"] = 0.85, ["unclear"] = 0.05 }));
        Assert.False(DecisionPolicy.AllCriteriaSatisfied([ok, bad]));
        Assert.True(DecisionPolicy.AllCriteriaSatisfied([ok]));
    }

    [Fact]
    public void Question_cannot_consume_another_answer()
    {
        Assert.True(DecisionPolicy.AnswersAreIndependent);
        var catalog = Catalog();
        var a = DecisionPolicy.Interpret(catalog.TryGet("passage.correction")!, NoulResponse("correction", 0.9));
        var b = DecisionPolicy.Interpret(catalog.TryGet("passage.commitment")!, NoulResponse("commitment", 0.9));
        // Interpretations do not embed each other's answers.
        Assert.Null(a.ChosenOption);
        Assert.Equal("correction", a.QuestionKey);
        Assert.Equal("commitment", b.QuestionKey);
    }

    [Fact]
    public void Missing_referents_yield_unclear()
    {
        Assert.Equal(DecisionPolarity.Unclear, DecisionPolicy.ResolveMissingReferents(anyCandidate: false));
        var def = Catalog().TryGet("reference.resolution")!;
        var interp = DecisionPolicy.Interpret(def, ChoiceResponse("resolution", "unclear",
            new Dictionary<string, double> { ["candidates"] = 0.2, ["none"] = 0.2, ["unclear"] = 0.6 }));
        Assert.Equal(DecisionPolarity.Unclear, interp.Polarity);
    }

    [Fact]
    public void Strong_urgency_cannot_grant_permission_or_override_conflict()
    {
        var def = Catalog().TryGet("passage.explicit_urgency")!;
        var urgency = DecisionPolicy.Interpret(def, NoulResponse("explicit_urgency", 0.99));
        Assert.Equal(DecisionPolarity.Positive, urgency.Polarity);
        Assert.False(DecisionPolicy.UrgencyGrantsPermission(urgency));
        Assert.False(DecisionPolicy.UrgencyOverridesConflict(urgency, ["conflict-1"]));
        Assert.False(DecisionPolicy.ResolveConflictByConfidence([urgency]));
    }

    [Fact]
    public void Choice_requires_win_probability_and_margin()
    {
        var def = Catalog().TryGet("candidate.case_relation")!;
        var weak = DecisionPolicy.Interpret(def, ChoiceResponse("case_relation", "yes",
            new Dictionary<string, double> { ["yes"] = 0.55, ["no"] = 0.45, ["unclear"] = 0.0 }));
        Assert.Equal(DecisionPolarity.Uncertain, weak.Polarity);

        var strong = DecisionPolicy.Interpret(def, ChoiceResponse("case_relation", "yes",
            new Dictionary<string, double> { ["yes"] = 0.85, ["no"] = 0.10, ["unclear"] = 0.05 }));
        Assert.Equal(DecisionPolarity.Positive, strong.Polarity);
        Assert.True(strong.Margin >= DecisionPolicy.ChoiceMargin);
    }
}
