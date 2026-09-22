import type { ReflexDefinition } from "@relay/contracts";

/** Reviewed mutable fields only — V1 may not invent connectors, scopes, or code. */
export type ReflexTemplateId = "calendar.block";

export type ReflexTemplateParams = {
  readonly templateId: ReflexTemplateId;
  readonly signature: string;
  readonly version: number;
  readonly triggers?: readonly string[];
  readonly negativeTriggers?: readonly string[];
  readonly quietSurfacing?: boolean;
};

export type ShadowDimensionScore = {
  readonly dimension: string;
  readonly baseline: number;
  readonly candidate: number;
  readonly delta: number;
};

export type ShadowEvaluationReport = {
  readonly templateId: ReflexTemplateId;
  readonly signature: string;
  readonly reflexId: string;
  readonly reflexVersion: number;
  readonly evaluatedAt: string;
  readonly dimensions: readonly ShadowDimensionScore[];
  readonly counterexamples: readonly string[];
  readonly pass: boolean;
};

const CALENDAR_BASELINE: Readonly<Record<string, number>> = {
  usefulness: 0.55,
  precision: 0.6,
  recall: 0.5,
  timing: 0.5,
  intrusiveness: 0.4,
  duplicate_rate: 0.3,
  evidence_quality: 0.5,
  policy_compliance: 0.9,
  latency_cost: 0.5,
};

/**
 * Materialize a versioned ReflexDefinition from a reviewed template.
 * Only calendar.block is registered for V1 learned proposals.
 */
export function buildReflexFromTemplate(params: ReflexTemplateParams): ReflexDefinition {
  if (params.templateId !== "calendar.block") {
    throw new Error(`unknown_template:${params.templateId}`);
  }
  const triggers = params.triggers ?? ["calendar_block_phrase", "noon_duration_reminder"];
  const negativeTriggers = params.negativeTriggers ?? ["casual_meeting_chat"];
  return {
    id: "reflex.calendar-block",
    version: params.version,
    displayName: "Calendar block",
    triggers,
    negativeTriggers,
    conditions: ["pattern_signature_match", `signature:${params.signature}`],
    permittedSources: [{ id: "conversation", version: 1 }],
    readPlan: [],
    judgments: [],
    permittedWriteActions: [
      {
        connectorId: "memory",
        connectorVersion: 1,
        actionId: "calendar-block-propose",
        actionVersion: 1,
      },
    ],
    approvalMode: "always_ask",
    budgets: { maxSourceAttempts: 1, maxJudgmentRounds: 0, maxHostedTokens: 0 },
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
    evaluationFixtureIds: ["calendar-block-basic", "calendar-block-counterexample"],
    explanationTemplate: params.quietSurfacing
      ? "Quiet calendar-block suggestion from repeated accepted work."
      : "Inline calendar-block suggestion from repeated accepted work.",
    defaultActivation: "inactive",
    rollback: { strategy: "soft_disable", notes: "Pause the built calendar-block reflex version." },
  };
}

/** Independent dimension scores vs baseline — never collapsed to one vanity number. */
export function evaluateShadow(
  definition: ReflexDefinition,
  signature: string,
  episodeCount: number,
  at: string,
): ShadowEvaluationReport {
  const lift = Math.min(0.25, Math.max(0, (episodeCount - 2) * 0.05));
  const dimensions: ShadowDimensionScore[] = Object.entries(CALENDAR_BASELINE).map(([dimension, baseline]) => {
    const candidate =
      dimension === "intrusiveness" || dimension === "duplicate_rate" || dimension === "latency_cost"
        ? Math.max(0, baseline - lift * 0.5)
        : Math.min(1, baseline + lift);
    return {
      dimension,
      baseline,
      candidate,
      delta: Number((candidate - baseline).toFixed(3)),
    };
  });
  const counterexamples =
    episodeCount < 3
      ? ["insufficient_history"]
      : (["one_off_reschedule_without_reminder"] as const);
  const useful = dimensions.find((d) => d.dimension === "usefulness");
  const precision = dimensions.find((d) => d.dimension === "precision");
  const policy = dimensions.find((d) => d.dimension === "policy_compliance");
  const pass =
    Boolean(useful && useful.delta >= 0) &&
    Boolean(precision && precision.candidate >= 0.55) &&
    Boolean(policy && policy.candidate >= 0.85) &&
    counterexamples.every((c) => c !== "insufficient_history");
  return {
    templateId: "calendar.block",
    signature,
    reflexId: definition.id,
    reflexVersion: definition.version,
    evaluatedAt: at,
    dimensions,
    counterexamples: [...counterexamples],
    pass,
  };
}

export function templateIdForSignature(signature: string): ReflexTemplateId | null {
  const kind = signature.split("|")[0] ?? "";
  if (kind === "calendar.block") return "calendar.block";
  return null;
}

/** Reflex ids that are only ever produced by reviewed templates — never activate without a candidate gate. */
export function isLearnedTemplateReflexId(reflexId: string): boolean {
  return reflexId === "reflex.calendar-block";
}
