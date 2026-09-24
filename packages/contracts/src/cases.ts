export type CaseOrigin = "direct" | "observed" | "dialogue";

export type CaseKind =
  | "remember"
  | "check"
  | "resolve"
  | "answer"
  | "organize"
  | "research"
  | "improve";

export type CaseStatus = "active" | "waiting" | "suspended" | "completed" | "cancelled" | "blocked" | "failed";

export type CasePhase =
  | "intake"
  | "detect"
  | "read"
  | "model"
  | "judge"
  | "decide"
  | "propose"
  | "awaiting_approval"
  | "execute"
  | "publish"
  | "done";

/**
 * One phased job for one input. `caseId` is the legacy execution id.
 * It is not a ProjectCase id. New readers should use `executionId`.
 */
export type CaseRecord = {
  readonly caseId: string;
  /** Same value as caseId. Present on records written after the Pass 1 migration. */
  readonly executionId?: string;
  readonly version: number;
  readonly origin: CaseOrigin;
  readonly kind: CaseKind;
  readonly status: CaseStatus;
  readonly phase: CasePhase;
  readonly priority: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly waitKind?: string;
  readonly parentCaseId?: string;
};

/** Canonical name for the per-input job. `caseId` remains the legacy key. */
export type ExecutionRecord = CaseRecord & {
  readonly executionId: string;
};

export function executionIdOf(record: CaseRecord): string {
  return record.executionId ?? record.caseId;
}

export type CaseEventType =
  | "case.created"
  | "case.phase_changed"
  | "case.waiting"
  | "case.resumed"
  | "case.completed"
  | "case.cancelled"
  | "judgment.requested"
  | "judgment.completed"
  | "judgment.failed"
  | "judgment.deferred"
  | "finding.published"
  | "operation.proposed"
  | "operation.completed";

export type CaseEvent = {
  readonly caseId: string;
  readonly caseVersion: number;
  readonly type: CaseEventType;
  readonly at: string;
  readonly payload: Record<string, unknown>;
};
