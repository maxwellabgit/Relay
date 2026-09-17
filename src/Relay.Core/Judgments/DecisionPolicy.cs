using System.Text.Json;

namespace Relay.Core.Judgments;

public enum DecisionPolarity
{
    Positive,
    Negative,
    Uncertain,
    Unclear,
    NoFit,
}

/// <summary>Coded interpretation of one judgment. Provider confidence is stored separately.</summary>
public sealed class DecisionInterpretation
{
    public required string DecisionId { get; init; }
    public required string QuestionKey { get; init; }
    public required string Primitive { get; init; }
    public DecisionPolarity Polarity { get; init; }
    public string? ChosenOption { get; init; }
    public double? Noul { get; init; }
    public double? Score { get; init; }
    public double? WinProbability { get; init; }
    public double? Margin { get; init; }
    /// <summary>Provider confidence — never used as permission or as sole conflict resolver.</summary>
    public double? ProviderConfidence { get; init; }
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }
    public IReadOnlyList<string> PreservedSourceRefs { get; init; } = [];
    public IReadOnlyList<string> ConflictIds { get; init; } = [];
    public string? BlockReason { get; init; }
    public bool GrantsPermission => false; // permissions are independent of semantics
}

/// <summary>
/// Combines typed judgments in code with explicit thresholds.
/// Does not treat model agreement as evidence; does not resolve conflicts by highest confidence.
/// </summary>
public static class DecisionPolicy
{
    public const double NoulPositive = 0.8;
    public const double NoulNegative = 0.2;
    public const double ChoiceWinProbability = 0.8;
    public const double ChoiceMargin = 0.2;

    public static DecisionInterpretation Interpret(
        DecisionDefinition definition,
        JudgmentResponse response,
        string? questionKey = null,
        IReadOnlyList<string>? sourceRefs = null,
        IReadOnlyList<string>? conflictIds = null)
    {
        if (!response.Ok)
        {
            return new DecisionInterpretation
            {
                DecisionId = definition.Id,
                QuestionKey = questionKey ?? definition.Questions.Keys.FirstOrDefault() ?? definition.Id,
                Primitive = definition.Primitive,
                Polarity = DecisionPolarity.Uncertain,
                BlockReason = response.ErrorCode ?? "judgment_failed",
                PreservedSourceRefs = sourceRefs ?? [],
                ConflictIds = conflictIds ?? [],
            };
        }

        var key = questionKey ?? definition.Questions.Keys.First();
        var primitive = definition.Questions.TryGetValue(key, out var q) ? q.Type : definition.Primitive;

        return primitive switch
        {
            JudgmentQuestionTypes.Noul => InterpretNoul(definition, key, response, sourceRefs, conflictIds),
            JudgmentQuestionTypes.Choice => InterpretChoice(definition, key, response, sourceRefs, conflictIds),
            JudgmentQuestionTypes.Score => InterpretScore(definition, key, response, sourceRefs, conflictIds),
            _ => new DecisionInterpretation
            {
                DecisionId = definition.Id,
                QuestionKey = key,
                Primitive = primitive,
                Polarity = DecisionPolarity.Uncertain,
                BlockReason = "unknown_primitive",
                PreservedSourceRefs = sourceRefs ?? [],
                ConflictIds = conflictIds ?? [],
            },
        };
    }

    private static DecisionInterpretation InterpretNoul(
        DecisionDefinition def, string key, JudgmentResponse response,
        IReadOnlyList<string>? sourceRefs, IReadOnlyList<string>? conflictIds)
    {
        var noul = response.TryGetNoul(key)?.Noul;
        var polarity = noul switch
        {
            >= NoulPositive => DecisionPolarity.Positive,
            <= NoulNegative => DecisionPolarity.Negative,
            _ => DecisionPolarity.Uncertain,
        };
        return new DecisionInterpretation
        {
            DecisionId = def.Id,
            QuestionKey = key,
            Primitive = JudgmentQuestionTypes.Noul,
            Polarity = polarity,
            Noul = noul,
            ProviderConfidence = null, // Noul has no confidence property
            PreservedSourceRefs = sourceRefs ?? [],
            ConflictIds = conflictIds ?? [],
        };
    }

    private static DecisionInterpretation InterpretChoice(
        DecisionDefinition def, string key, JudgmentResponse response,
        IReadOnlyList<string>? sourceRefs, IReadOnlyList<string>? conflictIds)
    {
        var ans = response.TryGetChoice(key);
        if (ans is null)
        {
            return new DecisionInterpretation
            {
                DecisionId = def.Id,
                QuestionKey = key,
                Primitive = JudgmentQuestionTypes.Choice,
                Polarity = DecisionPolarity.Uncertain,
                BlockReason = "missing_choice_answer",
                PreservedSourceRefs = sourceRefs ?? [],
                ConflictIds = conflictIds ?? [],
            };
        }

        var ordered = ans.Probabilities.OrderByDescending(kv => kv.Value).ToList();
        var win = ordered[0].Value;
        var second = ordered.Count > 1 ? ordered[1].Value : 0;
        var margin = win - second;
        var clear = win >= ChoiceWinProbability && margin >= ChoiceMargin;

        var option = ans.Choice;
        DecisionPolarity polarity;
        if (!clear)
            polarity = option is "unclear" or "insufficient" or "none" ? DecisionPolarity.Unclear : DecisionPolarity.Uncertain;
        else if (option is "unclear" or "insufficient")
            polarity = DecisionPolarity.Unclear;
        else if (option is "none" or "no" or "unrelated" or "unsupported" or "unsatisfied")
            polarity = DecisionPolarity.Negative;
        else if (option is "yes" or "supports" or "supported" or "satisfied" or "candidates" or "established" or "revision" or "compatible")
            polarity = DecisionPolarity.Positive;
        else if (option is "conflict" or "contradicts" or "conflicting")
            polarity = DecisionPolarity.Negative;
        else
            polarity = DecisionPolarity.Positive; // concrete labelled option under clear win

        return new DecisionInterpretation
        {
            DecisionId = def.Id,
            QuestionKey = key,
            Primitive = JudgmentQuestionTypes.Choice,
            Polarity = polarity,
            ChosenOption = option,
            WinProbability = win,
            Margin = margin,
            ProviderConfidence = ans.Confidence,
            Probabilities = ans.Probabilities,
            PreservedSourceRefs = sourceRefs ?? [],
            ConflictIds = conflictIds ?? [],
        };
    }

    private static DecisionInterpretation InterpretScore(
        DecisionDefinition def, string key, JudgmentResponse response,
        IReadOnlyList<string>? sourceRefs, IReadOnlyList<string>? conflictIds)
    {
        var ans = response.TryGetScore(key);
        if (ans is null)
        {
            return new DecisionInterpretation
            {
                DecisionId = def.Id,
                QuestionKey = key,
                Primitive = JudgmentQuestionTypes.Score,
                Polarity = DecisionPolarity.Uncertain,
                BlockReason = "missing_score_answer",
                PreservedSourceRefs = sourceRefs ?? [],
                ConflictIds = conflictIds ?? [],
            };
        }

        return new DecisionInterpretation
        {
            DecisionId = def.Id,
            QuestionKey = key,
            Primitive = JudgmentQuestionTypes.Score,
            Polarity = DecisionPolarity.Positive, // rank available; caller uses Score
            Score = ans.Score,
            ProviderConfidence = ans.Confidence,
            Probabilities = ans.Probabilities,
            PreservedSourceRefs = sourceRefs ?? [],
            ConflictIds = conflictIds ?? [],
        };
    }

    /// <summary>Urgency never grants permission or overrides recorded conflicts.</summary>
    public static bool UrgencyGrantsPermission(DecisionInterpretation urgency) => false;

    public static bool UrgencyOverridesConflict(DecisionInterpretation urgency, IReadOnlyList<string> conflictIds)
        => false;

    /// <summary>High provider confidence cannot authorize an unsupported claim.</summary>
    public static bool AllowClaim(DecisionInterpretation claimSupport)
    {
        if (claimSupport.BlockReason is not null) return false;
        if (claimSupport.ChosenOption is "unsupported" or "insufficient" or "unclear") return false;
        if (claimSupport.Polarity is DecisionPolarity.Negative or DecisionPolarity.Unclear or DecisionPolarity.Uncertain)
            return false;
        return claimSupport.ChosenOption == "supported" && claimSupport.Polarity == DecisionPolarity.Positive;
    }

    /// <summary>One unsatisfied/unclear criterion blocks completion — no averaging.</summary>
    public static bool AllCriteriaSatisfied(IEnumerable<DecisionInterpretation> criteria)
    {
        foreach (var c in criteria)
        {
            if (c.ChosenOption != "satisfied" || c.Polarity != DecisionPolarity.Positive)
                return false;
        }
        return true;
    }

    /// <summary>Answers are independent — one question's answer is never fed as another question's answer.</summary>
    public static bool AnswersAreIndependent => true;

    /// <summary>Missing referents → unclear, not an arbitrary pick.</summary>
    public static DecisionPolarity ResolveMissingReferents(bool anyCandidate)
        => anyCandidate ? DecisionPolarity.Positive : DecisionPolarity.Unclear;

    /// <summary>Ambiguous capability match must not pick arbitrarily when unclear.</summary>
    public static string? SelectCapability(DecisionInterpretation match, IReadOnlyList<string> candidates)
    {
        if (match.ChosenOption == "unclear" || match.Polarity == DecisionPolarity.Unclear)
            return null;
        if (match.ChosenOption == "none" || match.Polarity == DecisionPolarity.Negative)
            return null;
        if (match.ChosenOption == "candidates" && candidates.Count == 1 && match.Polarity == DecisionPolarity.Positive)
            return candidates[0];
        if (match.ChosenOption == "candidates" && candidates.Count != 1)
            return null; // ambiguous — require resolution, do not pick highest confidence
        return null;
    }

    /// <summary>Conflicts remain until evidence or authority resolves them — never by confidence ranking.</summary>
    public static bool ResolveConflictByConfidence(IEnumerable<DecisionInterpretation> sides) => false;
}
