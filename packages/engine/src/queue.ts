export type WorkItemType =
  | "source.final"
  | "case.resume"
  | "judgment.completed"
  | "model.completed"
  | "operation.completed"
  | "timer.due";

export type WorkItem = {
  readonly workId: string;
  readonly type: WorkItemType;
  readonly priority: number;
  readonly availableAt: string;
  readonly payload: Record<string, unknown>;
  readonly createdAt: string;
};

/** Direct Ask uses a higher priority lane but never pauses listening work. */
export const PRIORITY_DIRECT = 100;
export const PRIORITY_OBSERVED = 50;
export const PRIORITY_TIMER = 10;
