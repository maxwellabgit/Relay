import type { SourceSliceRef } from "./transcript.js";

export type CandidateKind =
  | "direct_request"
  | "durable_information"
  | "factual_claim"
  | "correction"
  | "commitment"
  | "open_question"
  | "acronym"
  | "birthday"
  | "none";

export type CandidateEventStatus = "new" | "ignored" | "routed" | "surfaced" | "resolved";

export type CandidateEvent = {
  readonly candidateEventId: string;
  readonly sourceEventId: string;
  readonly caseId: string;
  readonly kind: CandidateKind;
  readonly subjectRefs: readonly string[];
  readonly sourceSliceRefs: readonly SourceSliceRef[];
  readonly extractorVersion: string;
  readonly status: CandidateEventStatus;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Structural urgency reason when present; never free-form prose. */
  readonly urgencyReason?: AmbientUrgencyReason | null;
  /** Normalized subject key used for dedup / correction linking. */
  readonly subjectKey?: string | null;
};

export type AmbientUrgencyReason =
  | "deadline"
  | "safety"
  | "blocking_decision"
  | "data_loss"
  | "unsupported";

export type AmbientRoute =
  | "ignore"
  | "attach_evidence"
  | "propose_note"
  | "verify_claim"
  | "suggest_next_task"
  | "answer_directly"
  | "surface_quietly"
  | "interrupt";

export type AmbientTriageScores = {
  readonly worth_remembering: number;
  readonly possible_fact_claim: number;
  readonly possible_correction: number;
  readonly possible_commitment: number;
  readonly possible_open_question: number;
  readonly related_to_active_case: number;
  readonly interrupt_worthy: number;
};

export type AmbientRecommendationPrimary =
  | "save"
  | "verify"
  | "create_task"
  | "review";

export type AmbientRecommendationView = {
  readonly recommendationId: string;
  readonly candidateEventId: string;
  readonly caseId: string;
  readonly route: AmbientRoute;
  readonly title: string;
  readonly reason: string;
  readonly evidenceCount: number;
  readonly primary: AmbientRecommendationPrimary;
  readonly quiet: boolean;
};
