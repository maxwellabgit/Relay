export const RUNTIME_STAGES = [
  "source.accept",
  "case.create",
  "reflex.detect",
  "candidate.build",
  "judgment.request",
  "judgment.response",
  "model.request",
  "model.response",
  "policy.evaluate",
  "proposal.create",
  "memory.write",
  "episode.complete",
  "pattern.update",
  "review.evaluate",
  "run",
  "session",
  "work",
] as const;

export const RUNTIME_STATUSES = ["started", "completed", "failed", "waiting"] as const;

const EVENT_TYPES = new Set([
  "run.started",
  "run.ended",
  "session.started",
  "session.ended",
  "source.accepted",
  "source.rejected",
  "case.created",
  "answer.committed",
  "reflex.detected",
  "judgment.requested",
  "judgment.completed",
  "judgment.failed",
  "model.requested",
  "model.completed",
  "model.failed",
  "policy.evaluated",
  "memory.stored",
  "memory.rejected",
  "episode.recorded",
  "pattern.updated",
  "candidate.updated",
  "candidate.approved",
  "candidate.rejected",
  "review.created",
  "outcome.recorded",
  "work.failed",
  "compaction.completed",
]);

const REASON_CODES = new Set([
  "start",
  "open",
  "completed",
  "abandoned",
  "listening_off",
  "detected",
  "choice",
  "exact_glossary",
  "no_candidates",
  "explicit_user",
  "explicit_memory",
  "birthday_confirmed",
  "confirmation_required",
  "empty_expansion",
  "invalid_token",
  "invalid_person",
  "invalid_date",
  "conflict",
  "policy_pass",
  "below_choice_minimum",
  "below_choice_margin",
  "not_in_options",
  "invalid_response",
  "invalid_gate",
  "missing_choice",
  "invalid_probability",
  "distribution_sum",
  "choice_conflict",
  "missing_secret",
  "hosted_processing_disabled",
  "rate_limited",
  "timeout",
  "overloaded",
  "network",
  "authentication",
  "disabled",
  "not_authorized",
  "cancelled",
  "work_failed",
  "user_approval",
  "user_reject",
  "user_snooze",
  "benefit_pass",
  "benefit_below_threshold",
  "jev_unavailable",
  "model_unavailable",
  "model_disabled",
  "direct_answer",
  "below_usefulness",
  "no_match",
  "context_candidate",
  "bundled_dictionary",
  "explicit_memory",
  "exact_glossary",
  "no_template",
  "unresolved",
  "recommendation_only",
  "sessions",
  "reflexes",
  "episodes",
  "candidates",
  "episode_recorded",
  "no_episode",
  "retry",
  "dead_letter",
  "blocked",
  "pass",
  "fail",
  "none",
]);

const ID_FIELDS = [
  "runId",
  "sessionId",
  "episodeId",
  "caseId",
  "workId",
  "judgmentId",
  "receiptId",
  "reflexId",
  "decisionId",
  "captureSessionId",
  "workSessionId",
] as const;

export type RuntimeEventV2 = {
  readonly schemaVersion: 2;
  readonly sequence: number;
  readonly runId: string;
  readonly at: string;
  readonly eventType: string;
  readonly stage: string;
  readonly status: "started" | "completed" | "failed" | "waiting";
  readonly sessionId?: string;
  readonly episodeId?: string;
  readonly caseId?: string;
  readonly workId?: string;
  readonly judgmentId?: string;
  readonly receiptId?: string;
  readonly reflexId?: string;
  readonly decisionId?: string;
  readonly captureSessionId?: string;
  readonly workSessionId?: string;
  readonly reasonCode?: string;
  readonly durationMs?: number;
  readonly attempt?: number;
  readonly queueDepth?: number;
};

const ALLOWED = new Set([
  "schemaVersion",
  "sequence",
  "runId",
  "at",
  "eventType",
  "stage",
  "status",
  "sessionId",
  "episodeId",
  "caseId",
  "workId",
  "judgmentId",
  "receiptId",
  "reflexId",
  "decisionId",
  "captureSessionId",
  "workSessionId",
  "reasonCode",
  "durationMs",
  "attempt",
  "queueDepth",
]);

export function isRuntimeEvent(value: unknown): value is RuntimeEventV2 {
  if (!value || typeof value !== "object") return false;
  const row = value as Record<string, unknown>;
  if (Object.keys(row).some((key) => !ALLOWED.has(key))) return false;
  if (row.schemaVersion !== 2) return false;
  if (typeof row.sequence !== "number" || !Number.isInteger(row.sequence) || row.sequence < 1) return false;
  if (!isIso(row.at)) return false;
  if (typeof row.runId !== "string" || !/^run_[a-z0-9-]{1,40}$/.test(row.runId)) return false;
  if (typeof row.eventType !== "string" || !EVENT_TYPES.has(row.eventType)) return false;
  if (typeof row.stage !== "string" || !RUNTIME_STAGES.includes(row.stage as (typeof RUNTIME_STAGES)[number])) {
    return false;
  }
  if (typeof row.status !== "string" || !RUNTIME_STATUSES.includes(row.status as (typeof RUNTIME_STATUSES)[number])) {
    return false;
  }
  if (row.reasonCode != null && (typeof row.reasonCode !== "string" || !REASON_CODES.has(row.reasonCode))) return false;
  for (const field of ID_FIELDS) {
    if (field === "runId") continue;
    const item = row[field];
    if (item == null) continue;
    if (typeof item !== "string" || !isSafeId(item)) return false;
  }
  if (row.durationMs != null && !isCount(row.durationMs)) return false;
  if (row.attempt != null && !isCount(row.attempt)) return false;
  if (row.queueDepth != null && !isCount(row.queueDepth)) return false;
  return true;
}

function isIso(value: unknown): value is string {
  return typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/.test(value);
}

function isSafeId(value: string): boolean {
  if (value.length > 80 || /\s/.test(value) || /sentinel/i.test(value)) return false;
  return /^[a-z][a-z0-9._-]{0,78}$/.test(value);
}

function isCount(value: unknown): boolean {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

export function knownReason(code: string): string {
  return REASON_CODES.has(code) ? code : "work_failed";
}

export function backoffMs(attempt: number): number {
  return Math.min(8_000, 200 * 2 ** Math.max(0, attempt - 1));
}

export const JUDGMENT_MAX_ATTEMPTS = 3;
