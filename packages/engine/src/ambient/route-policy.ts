import type {
  AmbientRoute,
  AmbientTriageScores,
  AmbientUrgencyReason,
  CandidateKind,
} from "@relay/contracts";

/** Defaults keep ignore as the common ambient outcome. */
export const AMBIENT_THRESHOLDS = {
  worthRemembering: 0.72,
  factClaim: 0.72,
  correction: 0.7,
  commitment: 0.7,
  openQuestion: 0.75,
  relatedCase: 0.7,
  /** Interrupt requires a high bar plus a supported urgency reason. */
  interrupt: 0.9,
  quietSurface: 0.62,
  ambiguitySpread: 0.18,
} as const;

export type AmbientRouteDecision = {
  readonly route: AmbientRoute;
  readonly reasonCode: string;
  readonly probabilities: Readonly<Record<string, number>>;
  readonly thresholds: Readonly<Record<string, number>>;
  readonly quiet: boolean;
};

/**
 * Map parallel ambient Noul scores to an explicit route.
 * Ambiguity lowers urgency; it never broadens permissions.
 */
export function decideAmbientRoute(input: {
  readonly scores: AmbientTriageScores;
  readonly kind: CandidateKind;
  readonly urgencyReason?: AmbientUrgencyReason | null;
  readonly suppressed?: boolean;
  readonly hasActiveRelatedCase?: boolean;
}): AmbientRouteDecision {
  const scores = input.scores;
  const probabilities = { ...scores };
  const thresholds = {
    worth_remembering: AMBIENT_THRESHOLDS.worthRemembering,
    possible_fact_claim: AMBIENT_THRESHOLDS.factClaim,
    possible_correction: AMBIENT_THRESHOLDS.correction,
    possible_commitment: AMBIENT_THRESHOLDS.commitment,
    possible_open_question: AMBIENT_THRESHOLDS.openQuestion,
    related_to_active_case: AMBIENT_THRESHOLDS.relatedCase,
    interrupt_worthy: AMBIENT_THRESHOLDS.interrupt,
    quiet_surface: AMBIENT_THRESHOLDS.quietSurface,
  };

  if (input.suppressed) {
    return {
      route: "ignore",
      reasonCode: "suppressed",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (input.kind === "none" || input.kind === "birthday" || input.kind === "acronym") {
    return {
      route: "ignore",
      reasonCode: input.kind === "none" ? "no_candidate" : "handled_elsewhere",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  const ambiguous = isAmbiguous(scores);
  const interruptOk =
    scores.interrupt_worthy >= AMBIENT_THRESHOLDS.interrupt &&
    isSupportedUrgency(input.urgencyReason) &&
    !ambiguous;

  if (interruptOk) {
    return {
      route: "interrupt",
      reasonCode: `urgency:${input.urgencyReason}`,
      probabilities,
      thresholds,
      quiet: false,
    };
  }

  if (
    input.hasActiveRelatedCase &&
    scores.related_to_active_case >= AMBIENT_THRESHOLDS.relatedCase &&
    !ambiguous
  ) {
    return {
      route: "attach_evidence",
      reasonCode: "related_active_case",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    (input.kind === "correction" || scores.possible_correction >= AMBIENT_THRESHOLDS.correction) &&
    scores.worth_remembering >= AMBIENT_THRESHOLDS.quietSurface
  ) {
    return {
      route: "propose_note",
      reasonCode: "correction",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    (input.kind === "factual_claim" || scores.possible_fact_claim >= AMBIENT_THRESHOLDS.factClaim) &&
    scores.possible_fact_claim >= scores.worth_remembering
  ) {
    return {
      route: "verify_claim",
      reasonCode: "fact_claim",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    (input.kind === "commitment" || scores.possible_commitment >= AMBIENT_THRESHOLDS.commitment) &&
    scores.possible_commitment >= AMBIENT_THRESHOLDS.commitment
  ) {
    return {
      route: "suggest_next_task",
      reasonCode: "commitment",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    (input.kind === "durable_information" ||
      scores.worth_remembering >= AMBIENT_THRESHOLDS.worthRemembering) &&
    scores.worth_remembering >= AMBIENT_THRESHOLDS.worthRemembering
  ) {
    return {
      route: "propose_note",
      reasonCode: "worth_remembering",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    input.kind === "open_question" ||
    scores.possible_open_question >= AMBIENT_THRESHOLDS.openQuestion
  ) {
    return {
      route: "surface_quietly",
      reasonCode: "open_question",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  if (
    !ambiguous &&
    Math.max(
      scores.worth_remembering,
      scores.possible_commitment,
      scores.possible_fact_claim,
      scores.possible_correction,
    ) >= AMBIENT_THRESHOLDS.quietSurface
  ) {
    return {
      route: "surface_quietly",
      reasonCode: "quiet_signal",
      probabilities,
      thresholds,
      quiet: true,
    };
  }

  return {
    route: "ignore",
    reasonCode: ambiguous ? "ambiguous_ignore" : "below_threshold",
    probabilities,
    thresholds,
    quiet: true,
  };
}

function isSupportedUrgency(reason: AmbientUrgencyReason | null | undefined): boolean {
  return (
    reason === "deadline" ||
    reason === "safety" ||
    reason === "blocking_decision" ||
    reason === "data_loss"
  );
}

function isAmbiguous(scores: AmbientTriageScores): boolean {
  const values = [
    scores.worth_remembering,
    scores.possible_fact_claim,
    scores.possible_correction,
    scores.possible_commitment,
    scores.possible_open_question,
    scores.interrupt_worthy,
  ].sort((a, b) => b - a);
  const top = values[0] ?? 0;
  const second = values[1] ?? 0;
  return top - second < AMBIENT_THRESHOLDS.ambiguitySpread && top < AMBIENT_THRESHOLDS.worthRemembering;
}
