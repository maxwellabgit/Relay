using Relay.Core.Artifacts;
using Relay.Core.Connectors;

namespace Relay.Core.Reflexes;

/// <summary>
/// The four built-in alpha Reflex definitions. Handlers are not wired yet.
/// All source and action references are exact versions.
/// </summary>
public static class ReflexCatalog
{
    private static ConnectorRef C(string id) => new(id, 1);
    private static ConnectorActionRef A(string connectorId, string actionId) => new(connectorId, 1, actionId, 1);
    private static JudgmentDefinitionRef J(string id) => new(id, 1);

    public static IReadOnlyList<ReflexDefinition> AlphaReflexes { get; } =
    [
        RememberBirthday(),
        VerifyTechnicalClaim(),
        PreserveImportantInformation(),
        ResolveAcronym(),
    ];

    public static ReflexDefinition Require(string id, int version = 1) =>
        AlphaReflexes.Single(r => r.Id == id && r.Version == version);

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
        PermittedSources: [C("conversation"), C("memory"), C("google-calendar")],
        ReadPlan:
        [
            A("memory", "memory.search"),
            A("google-calendar", "google-calendar.events-search"),
            A("google-calendar", "google-calendar.events-list"),
        ],
        Judgments:
        [
            J("birthday-statement"),
            J("person-candidate-choice"),
            J("date-meaning-choice"),
        ],
        PermittedWriteActions: [A("google-calendar", "google-calendar.event-create")],
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
        Rollback: new ReflexRollbackPolicy(
            RollbackStrategy.CompensatingAction,
            CompensatingAction: A("google-calendar", "google-calendar.event-update"),
            Notes: "Soft-cancel by updating the created event title/notes; alpha does not delete calendar events."));

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
            C("memory"),
            C("conversation"),
            C("gmail"),
            C("google-calendar"),
            C("github"),
            C("public-web"),
            C("wikipedia"),
        ],
        ReadPlan:
        [
            A("memory", "memory.search"),
            A("conversation", "conversation.search"),
            A("gmail", "gmail.messages-search"),
            A("google-calendar", "google-calendar.events-search"),
            A("github", "github.issues-search"),
            A("github", "github.pull-requests-search"),
            A("github", "github.code-search"),
            A("github", "github.content-get"),
            A("github", "github.comments-list"),
            A("public-web", "public-web.search"),
            A("wikipedia", "wikipedia.search"),
        ],
        Judgments:
        [
            J("claim-checkability"),
            J("claim-source-choice"),
            J("evidence-relevance"),
            J("claim-support"),
            J("interruption-materiality"),
        ],
        PermittedWriteActions: [],
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
        Rollback: new ReflexRollbackPolicy(RollbackStrategy.None));

    private static ReflexDefinition PreserveImportantInformation() => new(
        Id: "reflex.preserve-important-information",
        Version: 1,
        DisplayName: "Preserve important information",
        Triggers:
        [
            "durable_fact_candidate",
            "decision_or_constraint_stated",
            "configuration_or_correction",
            "observed_source_item",
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
            C("conversation"),
            C("memory"),
            C("gmail"),
            C("google-calendar"),
            C("google-sheets"),
            C("github"),
        ],
        ReadPlan:
        [
            A("memory", "memory.search"),
            A("memory", "memory.note-get"),
        ],
        Judgments:
        [
            J("durable-importance"),
            J("project-relevance"),
            J("note-equivalence-or-conflict"),
        ],
        PermittedWriteActions: [A("memory", "memory.note-create")],
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
        Rollback: new ReflexRollbackPolicy(
            RollbackStrategy.CompensatingAction,
            CompensatingAction: A("memory", "memory.note-create"),
            Notes: "Supersede the created note with a tombstone/correction note; notes are never silently deleted."));

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
            C("memory"),
            C("conversation"),
            C("gmail"),
            C("github"),
            C("public-web"),
            C("wikipedia"),
        ],
        ReadPlan:
        [
            A("memory", "memory.search"),
            A("conversation", "conversation.search"),
            A("gmail", "gmail.messages-search"),
            A("github", "github.issues-search"),
            A("github", "github.code-search"),
            A("github", "github.content-get"),
            A("public-web", "public-web.search"),
            A("wikipedia", "wikipedia.search"),
        ],
        Judgments:
        [
            J("acronym-candidate-choice"),
            J("public-search-warranted"),
        ],
        PermittedWriteActions: [A("memory", "memory.acronym-remember")],
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
        Rollback: new ReflexRollbackPolicy(
            RollbackStrategy.CompensatingAction,
            CompensatingAction: A("memory", "memory.acronym-remember"),
            Notes: "Remembering an expansion can be superseded by a corrected glossary write."));
}
