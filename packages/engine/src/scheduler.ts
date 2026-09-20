import type { WorkItem, WorkItemType } from "./queue.js";

export type Clock = {
  now(): Date;
};

export type IdFactory = {
  next(prefix: string): string;
};

export type WorkStore = {
  enqueue(item: WorkItem): Promise<void>;
  claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null>;
  complete(workId: string): Promise<void>;
  requeue(workId: string, availableAt: string, payload?: Record<string, unknown>): Promise<void>;
};

export class Scheduler {
  constructor(
    private readonly store: WorkStore,
    private readonly clock: Clock,
    private readonly owner: string,
    private readonly leaseMs = 30_000,
  ) {}

  async enqueue(
    type: WorkItemType,
    payload: Record<string, unknown>,
    priority: number,
    ids: IdFactory,
    delayMs = 0,
  ): Promise<string> {
    const now = this.clock.now();
    const availableAt = new Date(now.getTime() + delayMs).toISOString();
    const workId = ids.next("work");
    await this.store.enqueue({
      workId,
      type,
      priority,
      availableAt,
      payload,
      createdAt: now.toISOString(),
    });
    return workId;
  }

  async claim(): Promise<WorkItem | null> {
    return this.store.claimNext(this.clock.now().toISOString(), this.owner, this.leaseMs);
  }

  async complete(workId: string): Promise<void> {
    await this.store.complete(workId);
  }
}
