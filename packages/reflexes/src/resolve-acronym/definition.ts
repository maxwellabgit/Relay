import type { ReflexDefinition } from "@relay/contracts";

export const resolveAcronymDefinition: ReflexDefinition = {
  id: "reflex.resolve-acronym",
  version: 1,
  displayName: "Resolve acronym",
  triggers: ["uppercase_token"],
  negativeTriggers: ["common_word_all_caps"],
  conditions: ["has_candidate_expansions_or_ask"],
  permittedSources: [{ id: "conversation", version: 1 }],
  readPlan: [],
  judgments: [{ id: "judgment.acronym-choice", version: 1 }],
  permittedWriteActions: [
    {
      connectorId: "memory",
      connectorVersion: 1,
      actionId: "acronym-remember",
      actionVersion: 1,
    },
  ],
  approvalMode: "always_ask",
  budgets: { maxSourceAttempts: 3, maxJudgmentRounds: 1, maxHostedTokens: 2000 },
  retryPolicy: { maxAttempts: 2, initialBackoffMs: 250, maxBackoffMs: 2000 },
  evaluationFixtureIds: ["acronym-basic"],
  explanationTemplate: "Resolved {token} using glossary and judgment policy.",
  defaultActivation: "active",
  rollback: { strategy: "none" },
};
