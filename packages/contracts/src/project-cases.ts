export type ProjectCaseStatus = "active" | "archived";

export type ProjectCaseEntry = {
  readonly entryId: string;
  readonly kind: "fact" | "decision" | "question" | "problem" | "pattern";
  readonly text: string;
  readonly updatedAt: string;
};

export type ProjectCaseReference = {
  readonly referenceId: string;
  readonly provider: string;
  readonly resourceId: string;
  readonly url: string | null;
  readonly revision: string | null;
  readonly checkedAt: string | null;
  readonly status: "linked" | "revoked" | "missing";
  readonly authorization: "selected" | "unselected";
};

export type ProjectCaseRule = {
  readonly ruleId: string;
  readonly version: number;
  readonly config: Readonly<Record<string, unknown>>;
};

export type ProjectCaseRevision = {
  readonly revisionId: string;
  readonly version: number;
  readonly op: string;
  readonly at: string;
  readonly undoOf: string | null;
};

export type ProjectCase = {
  readonly projectCaseId: string;
  readonly alias: string;
  readonly status: ProjectCaseStatus;
  readonly version: number;
  readonly intent: string;
  readonly entries: readonly ProjectCaseEntry[];
  readonly references: readonly ProjectCaseReference[];
  readonly rules: readonly ProjectCaseRule[];
  readonly createdAt: string;
  readonly updatedAt: string;
};

export type ProjectCaseView = {
  readonly projectCaseId: string;
  readonly alias: string;
  readonly status: ProjectCaseStatus;
  readonly version: number;
  readonly intent: string;
  readonly entryCount: number;
  readonly referenceCount: number;
  readonly pendingVerify: number;
};

export type CaseActivityKind = "read" | "write" | "finding" | "execution" | "verify";

/** Short-lived UI emphasis. Not a stored fact. */
export type CaseActivity = {
  readonly projectCaseId: string;
  readonly kind: CaseActivityKind;
  readonly at: string;
  readonly executionId?: string;
  readonly verifyId?: string;
};

export type HeadsUpNotice = {
  readonly id: string;
  readonly tone: "info" | "contradiction";
  readonly text: string;
  readonly at: string;
  readonly projectCaseId?: string;
  readonly verifyId?: string;
};
