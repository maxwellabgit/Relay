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
  | "judge"
  | "decide"
  | "propose"
  | "awaiting_approval"
  | "execute"
  | "publish"
  | "done";

export type CaseRecord = {
  readonly caseId: string;
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
