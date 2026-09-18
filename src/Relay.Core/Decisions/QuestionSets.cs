using Relay.Core.Judgments;

namespace Relay.Core.Decisions;

/// <summary>Immutable v0.1 question-set assets. Instructions are never taken from transcript content.</summary>
public static class QuestionSets
{
    public const string ConversationScreenId = "conversation.screen";
    public const string ConversationScreenVersion = "v1";
    public const string DirectRouteId = "direct.route";
    public const string DirectRouteVersion = "v1";
    public const string AcronymSelectId = "acronym.select";
    public const string AcronymSelectVersion = "v1";
    public const string NoteSupportId = "note.support";
    public const string NoteSupportVersion = "v1";

    public static QuestionSetRegistry CreateV1() => new([
        ConversationScreenV1(),
        DirectRouteV1(),
        // Acronym and note-support sets are registered with placeholder criteria;
        // runtime fills Choice criteria from deterministic candidates before dispatch.
        AcronymSelectV1Placeholder(),
        NoteSupportV1(),
    ]);

    public static QuestionSetDefinition ConversationScreenV1() => new()
    {
        Id = ConversationScreenId,
        Version = ConversationScreenVersion,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["contains_durable_decision"] = new NoulQuestion
            {
                Instructions = "Do the unread transcript segments contain a durable decision or settled fact worth remembering?",
            },
            ["contains_actionable_commitment"] = new NoulQuestion
            {
                Instructions = "Do the unread transcript segments contain an actionable commitment someone made to do specific work?",
            },
            ["contains_correction"] = new NoulQuestion
            {
                Instructions = "Do the unread transcript segments correct or revise a prior claim about the same subject?",
            },
            ["contains_unresolved_term_request"] = new NoulQuestion
            {
                Instructions = "Do the unread transcript segments ask what an acronym or technical term means, or leave such a term unresolved?",
            },
            ["attention"] = new ChoiceQuestion
            {
                Instructions = "What attention level should the feed use for these unread segments?",
                RequireNoMatch = false,
                Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ambient"] = "Subtle, low-priority observation.",
                    ["persistent"] = "Useful standing item such as a commitment, acronym, or connection.",
                    ["alert"] = "Likely contradiction, deadline, or significant correction.",
                },
            },
        },
    };

    public static QuestionSetDefinition DirectRouteV1() => new()
    {
        Id = DirectRouteId,
        Version = DirectRouteVersion,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["route"] = new ChoiceQuestion
            {
                Instructions = "Which route best fits this direct user request?",
                Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["answer"] = "Answer using local evidence.",
                    ["remember"] = "Capture a note or durable fact.",
                    ["organize"] = "Create or update a task/organization item.",
                    ["clarify"] = "Ask a clarifying question.",
                    [ChoiceQuestion.NoMatch] = "None of the allowed routes fit.",
                },
            },
        },
    };

    public static QuestionSetDefinition AcronymSelectV1Placeholder() => new()
    {
        Id = AcronymSelectId,
        Version = AcronymSelectVersion,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["select"] = new ChoiceQuestion
            {
                Instructions = "Which glossary expansion best fits this acronym in the supplied project context?",
                Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ChoiceQuestion.NoMatch] = "None of the candidate expansions fit.",
                },
            },
        },
    };

    public static QuestionSetDefinition NoteSupportV1() => new()
    {
        Id = NoteSupportId,
        Version = NoteSupportVersion,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["supported"] = new NoulQuestion
            {
                Instructions = "Is every material claim in the draft note supported by the supplied source excerpts?",
            },
            ["unsupported_claim"] = new NoulQuestion
            {
                Instructions = "Does the draft introduce a person, date, commitment, or causal claim not present in the sources?",
            },
        },
    };

    /// <summary>Clone acronym.select with runtime candidate criteria; instructions stay asset-owned.</summary>
    public static QuestionSetDefinition BuildAcronymSelect(IReadOnlyDictionary<string, string> candidateCriteria)
    {
        ArgumentNullException.ThrowIfNull(candidateCriteria);
        var criteria = new Dictionary<string, string>(candidateCriteria, StringComparer.Ordinal)
        {
            [ChoiceQuestion.NoMatch] = "None of the candidate expansions fit.",
        };
        return new QuestionSetDefinition
        {
            Id = AcronymSelectId,
            Version = AcronymSelectVersion,
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["select"] = new ChoiceQuestion
                {
                    Instructions = "Which glossary expansion best fits this acronym in the supplied project context?",
                    RequireNoMatch = true,
                    Criteria = criteria,
                },
            },
        };
    }
}
