import type {
  ReflexContext,
  ReflexDefinition,
  ReflexModule,
  ReflexResult,
  SourceEvent,
  TriggerCandidate,
} from "@relay/contracts";

export function phraseDefinition(input: {
  readonly id: string;
  readonly displayName: string;
  readonly trigger: string;
  readonly explanationTemplate: string;
}): ReflexDefinition {
  return {
    id: input.id,
    version: 1,
    displayName: input.displayName,
    triggers: [input.trigger],
    negativeTriggers: [],
    conditions: ["explicit_phrase"],
    permittedSources: [{ id: "conversation", version: 1 }],
    readPlan: [],
    judgments: [],
    permittedWriteActions: [
      {
        connectorId: "memory",
        connectorVersion: 1,
        actionId: "local-memory",
        actionVersion: 1,
      },
    ],
    approvalMode: "always_ask",
    budgets: { maxSourceAttempts: 1, maxJudgmentRounds: 0, maxHostedTokens: 0 },
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
    evaluationFixtureIds: [input.id],
    explanationTemplate: input.explanationTemplate,
    defaultActivation: "active",
    rollback: { strategy: "none" },
  };
}

export function detectPhrase(
  definition: ReflexDefinition,
  pattern: RegExp,
  event: SourceEvent,
): TriggerCandidate[] {
  const match = event.text.trim().match(pattern);
  const token = match?.[1]?.trim();
  if (!token) return [];
  return [
    {
      reflexId: definition.id,
      reflexVersion: definition.version,
      token,
      start: 0,
      end: event.text.length,
      reason: definition.triggers[0] ?? "phrase",
    },
  ];
}

export function findingFor(context: ReflexContext, summary: string): ReflexResult {
  return {
    type: "finding",
    summary,
    sourceRefs: context.triggerSourceRefs,
    judgmentIds: [],
    evidenceDrafts: [],
  };
}

export function phraseModule(
  definition: ReflexDefinition,
  pattern: RegExp,
  summary: (token: string) => string,
): ReflexModule {
  return {
    definition,
    detect(event: SourceEvent) {
      return detectPhrase(definition, pattern, event);
    },
    async evaluate(context: ReflexContext) {
      const token = context.triggerToken?.trim() ?? "";
      if (!token) {
        return {
          type: "no_action",
          summary: "empty",
          sourceRefs: [],
          judgmentIds: [],
        };
      }
      return findingFor(context, summary(token));
    },
  };
}
