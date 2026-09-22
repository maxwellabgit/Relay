import { PRIORITY_OBSERVED, type WorkItem } from "../queue.js";
import type { Clock, IdFactory, Scheduler } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import { sleep, type EngineTrace } from "../engine-helpers.js";
import type { CaseRuntime } from "./CaseRuntime.js";
import type { JudgmentService, WorkDisposition } from "../judgments/JudgmentService.js";
import type { ToolBroker } from "../tools/ToolBroker.js";
import { runInTransaction } from "../transactions.js";

export type WorkDispatcherDeps = {
  readonly store: EngineStore;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly scheduler: Scheduler;
  readonly cases: CaseRuntime;
  readonly judgments: JudgmentService;
  readonly tools: ToolBroker;
  readonly trace: EngineTrace;
  readonly emitSnapshot: () => Promise<void>;
  readonly setActiveCaseId: (id: string | null) => void;
  readonly setActiveEpisodeId: (id: string | null) => void;
  readonly isRunning: () => boolean;
};

export class WorkDispatcher {
  constructor(private readonly deps: WorkDispatcherDeps) {}

  async runLoop(signal: AbortSignal): Promise<void> {
    while (!signal.aborted && this.deps.isRunning()) {
      const item = await this.deps.scheduler.claim();
      if (!item) {
        await sleep(25, signal);
        continue;
      }
      try {
        const disposition = await this.process(item);
        if (disposition.kind === "retry") {
          const available = new Date(this.deps.clock.now().getTime() + disposition.delayMs).toISOString();
          await this.deps.store.requeue(item.workId, available, disposition.payload);
        } else if (disposition.kind === "dead") {
          await this.deps.store.deadLetter(item.workId, disposition.reasonCode, this.deps.clock.now().toISOString());
        } else {
          await this.deps.scheduler.complete(item.workId);
        }
        await this.deps.emitSnapshot();
      } catch {
        await this.deps.store.deadLetter(item.workId, "work_failed", this.deps.clock.now().toISOString());
        await this.deps.trace.emit({ type: "work.failed", stage: "work", status: "failed", reasonCode: "work_failed" });
      } finally {
        this.deps.setActiveCaseId(null);
        this.deps.setActiveEpisodeId(null);
      }
    }
  }

  async process(item: WorkItem): Promise<WorkDisposition> {
    this.deps.setActiveCaseId(typeof item.payload.caseId === "string" ? item.payload.caseId : null);
    switch (item.type) {
      case "source.final":
        return this.deps.cases.onSourceFinal(item);
      case "judgment.requested":
        return this.deps.judgments.onJudgmentRequested(item);
      case "model.requested":
        return this.deps.cases.onModelRequested(item);
      case "tool.route":
        return this.deps.tools.onToolRoute(item);
      case "tool.execute":
        return this.deps.tools.onToolExecute(item);
      case "case.resume":
        await this.onCaseResume(item);
        return { kind: "complete" };
      case "judgment.completed":
      case "model.completed":
      case "operation.completed":
        await this.onCompletion(item);
        return { kind: "complete" };
      case "timer.due":
        await this.onTimer(item);
        return { kind: "complete" };
    }
  }

  private async onCaseResume(item: WorkItem): Promise<void> {
    const caseId = String(item.payload.caseId);
    const expectedVersion = Number(item.payload.caseVersion);
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.version !== expectedVersion) return;
    await runInTransaction(this.deps.store, async () => {
      await this.deps.store.updateCase(caseId, expectedVersion, {
        status: "active",
        waitKind: null,
        phase: "detect",
        at: this.deps.clock.now().toISOString(),
      });
      await this.deps.store.appendDomainEvent("case.resumed", this.deps.clock.now().toISOString(), {
        caseId,
        expectedVersion,
        workId: item.workId,
      });
    });
  }

  private async onCompletion(item: WorkItem): Promise<void> {
    const caseId = String(item.payload.caseId);
    const expectedVersion = Number(item.payload.expectedCaseVersion);
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.version !== expectedVersion) {
      await this.deps.store.appendDomainEvent(
        "completion.stale",
        this.deps.clock.now().toISOString(),
        { caseId, expectedVersion, workType: item.type },
      );
      return;
    }

    if (item.payload.wait === true) {
      await this.deps.store.updateCase(caseId, expectedVersion, {
        status: "waiting",
        waitKind: String(item.payload.waitKind ?? item.type),
        at: this.deps.clock.now().toISOString(),
      });
      return;
    }

    await this.deps.store.updateCase(caseId, expectedVersion, {
      status: "active",
      waitKind: null,
      phase: "decide",
      at: this.deps.clock.now().toISOString(),
    });
  }

  private async onTimer(item: WorkItem): Promise<void> {
    const caseId = String(item.payload.caseId);
    const expectedVersion = Number(item.payload.caseVersion);
    await this.deps.scheduler.enqueue(
      "case.resume",
      { caseId, caseVersion: expectedVersion },
      PRIORITY_OBSERVED,
      this.deps.ids,
      0,
      { parentWorkId: item.workId, correlationId: caseId },
    );
  }
}
