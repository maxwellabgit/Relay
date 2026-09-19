namespace Relay.Core.Reflexes;

/// <summary>
/// The four built-in alpha Reflex definitions. Handlers are not wired yet.
/// </summary>
public static class ReflexCatalog
{
    public static IReadOnlyList<ReflexDefinition> AlphaReflexes { get; } =
    [
        RememberBirthday(),
        VerifyTechnicalClaim(),
        PreserveImportantInformation(),
        ResolveAcronym(),
    ];

    public static ReflexDefinition Require(string id) =>
        AlphaReflexes.Single(r => r.Id == id);

    private static ReflexDefinition RememberBirthday() => new(
        Id: "reflex.remember-birthday",
        Version: 1,
        DisplayName: "Remember birthdays",
        Triggers:
        [
            "final_transcript_birthday_statement",
            "direct_request_remember_birthday",
        ],
        NegativeTriggers:
        [
            "hypothetical_birthday",
            "fiction_or_roleplay",
        ],
        Conditions:
        [
            "person_and_date_candidates_present",
            "birthdays_calendar_selected",
        ],
        PermittedSources:
        [
            "conversation",
            "memory",
            "google-calendar",
        ],
        ReadPlan:
        [
            "memory.search",
            "google-calendar.events-list",
        ],
        JudgmentDefinitionIds:
        [
            "birthday-statement@1",
            "person-candidate-choice@1",
            "date-meaning-choice@1",
        ],
        PermittedWriteActionIds:
        [
            "google-calendar.event-create",
        ],
        ApprovalMode: ApprovalMode.AlwaysAsk,
        Budgets: new ReflexBudgets(MaxSourceAttempts: 2, MaxJudgmentRounds: 2, MaxHostedTokens: 4_000),
        RetryPolicy: new ReflexRetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromSeconds(2), MaxBackoff: TimeSpan.FromSeconds(30)),
        EvaluationFixtureIds:
        [
            "fixture.birthday.explicit-statement",
            "fixture.birthday.ambiguous-person",
            "fixture.birthday.existing-event",
        ],
        ExplanationTemplate: "Propose an annual all-day birthday event for {person} on {MM-dd} with a 1,440-minute reminder.",
        DefaultActivation: ReflexActivationState.Inactive,
        SupportsRollback: true);

    private static ReflexDefinition VerifyTechnicalClaim() => new(
        Id: "reflex.verify-technical-claim",
        Version: 1,
        DisplayName: "Verify technical claims",
        Triggers:
        [
            "checkable_technical_claim",
            "direct_request_verify_claim",
        ],
        NegativeTriggers:
        [
            "opinion_or_preference",
            "uncheckable_prediction",
        ],
        Conditions:
        [
            "claim_is_specific",
            "at_least_one_eligible_source",
        ],
        PermittedSources:
        [
            "memory",
            "conversation",
            "gmail",
            "google-calendar",
            "github",
            "public-web",
            "wikipedia",
        ],
        ReadPlan:
        [
            "memory.search",
            "gmail.messages-list",
            "github.issues-list",
            "public-web.search",
            "wikipedia.search",
        ],
        JudgmentDefinitionIds:
        [
            "claim-checkability@1",
            "claim-source-choice@1",
            "evidence-relevance@1",
            "claim-support@1",
            "interruption-materiality@1",
        ],
        PermittedWriteActionIds: [],
        ApprovalMode: ApprovalMode.AlwaysAsk,
        Budgets: new ReflexBudgets(MaxSourceAttempts: 3, MaxJudgmentRounds: 2, MaxHostedTokens: 8_000),
        RetryPolicy: new ReflexRetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.FromSeconds(1), MaxBackoff: TimeSpan.FromSeconds(1)),
        EvaluationFixtureIds:
        [
            "fixture.claim.supported",
            "fixture.claim.contradicted",
            "fixture.claim.insufficient",
        ],
        ExplanationTemplate: "Report whether the claim is supported, contradicted, or insufficient with exact citations.",
        DefaultActivation: ReflexActivationState.Inactive,
        SupportsRollback: false);

    private static ReflexDefinition PreserveImportantInformation() => new(
        Id: "reflex.preserve-important-information",
        Version: 1,
        DisplayName: "Preserve important information",
        Triggers:
        [
            "durable_fact_candidate",
            "decision_or_constraint_stated",
            "configuration_or_correction",
        ],
        NegativeTriggers:
        [
            "ephemeral_status_update",
            "already_captured_duplicate",
        ],
        Conditions:
        [
            "candidate_extractable",
            "local_memory_available",
        ],
        PermittedSources:
        [
            "conversation",
            "memory",
        ],
        ReadPlan:
        [
            "memory.search",
            "memory.note-get",
        ],
        JudgmentDefinitionIds:
        [
            "durable-importance@1",
            "project-relevance@1",
            "note-equivalence-or-conflict@1",
        ],
        PermittedWriteActionIds:
        [
            "memory.note-create",
        ],
        ApprovalMode: ApprovalMode.StandingGrantEligible,
        Budgets: new ReflexBudgets(MaxSourceAttempts: 2, MaxJudgmentRounds: 2, MaxHostedTokens: 4_000),
        RetryPolicy: new ReflexRetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromSeconds(1), MaxBackoff: TimeSpan.FromSeconds(15)),
        EvaluationFixtureIds:
        [
            "fixture.note.new-important",
            "fixture.note.equivalent-link",
            "fixture.note.conflict",
        ],
        ExplanationTemplate: "Preserve a source-linked local note for {candidate} unless an equivalent note already exists.",
        DefaultActivation: ReflexActivationState.Inactive,
        SupportsRollback: true);

    private static ReflexDefinition ResolveAcronym() => new(
        Id: "reflex.resolve-acronym",
        Version: 1,
        DisplayName: "Resolve acronyms",
        Triggers:
        [
            "plausible_acronym_token",
            "direct_request_expand_acronym",
        ],
        NegativeTriggers:
        [
            "common_english_word",
            "denied_token",
        ],
        Conditions:
        [
            "token_passes_allow_deny_list",
        ],
        PermittedSources:
        [
            "memory",
            "conversation",
            "gmail",
            "github",
            "public-web",
            "wikipedia",
        ],
        ReadPlan:
        [
            "memory.search",
            "gmail.messages-list",
            "github.issues-list",
            "public-web.search",
            "wikipedia.search",
        ],
        JudgmentDefinitionIds:
        [
            "acronym-candidate-choice@1",
            "public-search-warranted@1",
        ],
        PermittedWriteActionIds:
        [
            "memory.acronym-remember",
        ],
        ApprovalMode: ApprovalMode.AlwaysAsk,
        Budgets: new ReflexBudgets(MaxSourceAttempts: 4, MaxJudgmentRounds: 2, MaxHostedTokens: 4_000),
        RetryPolicy: new ReflexRetryPolicy(MaxAttempts: 1, InitialBackoff: TimeSpan.FromSeconds(1), MaxBackoff: TimeSpan.FromSeconds(1)),
        EvaluationFixtureIds:
        [
            "fixture.acronym.glossary-hit",
            "fixture.acronym.ambiguous-candidates",
            "fixture.acronym.public-fallback",
        ],
        ExplanationTemplate: "Resolve {token} from supplied candidates without inventing an expansion.",
        DefaultActivation: ReflexActivationState.Inactive,
        SupportsRollback: false);
}
