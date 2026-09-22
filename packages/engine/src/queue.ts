export type WorkItemType =
  | "source.final"
  | "case.resume"
  | "judgment.requested"
  | "judgment.completed"
  | "model.requested"
  | "model.completed"
  | "tool.route"
  | "tool.execute"
  | "operation.completed"
  | "timer.due";

export type WorkItem = {
  readonly workId: string;
  readonly type: WorkItemType;
  readonly priority: number;
  readonly availableAt: string;
  readonly payload: Record<string, unknown>;
  readonly createdAt: string;
  /** Parent work that enqueued this item, when known. */
  readonly parentWorkId?: string;
  /** Stable correlation key (typically caseId) spanning a work chain. */
  readonly correlationId?: string;
};

export type WorkCorrelation = {
  readonly parentWorkId?: string;
  readonly correlationId?: string;
};

/** Direct Ask uses a higher priority lane but never pauses listening work. */
export const PRIORITY_DIRECT = 100;
export const PRIORITY_OBSERVED = 50;
export const PRIORITY_TIMER = 10;
