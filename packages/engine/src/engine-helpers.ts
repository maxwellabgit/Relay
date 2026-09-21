import type { RuntimeEventV2 } from "./runtime-events.js";
import { knownReason } from "./runtime-events.js";
import type { EngineStore } from "./store.js";
import type { RuntimeRecorder } from "./diagnostics/RuntimeRecorder.js";

export function encodeText(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function feedItemId(caseId: string, role: string): string {
  return `feed_${caseId}_${role}`;
}

export function modelWorkId(caseId: string): string {
  return `work_${caseId}_model`;
}

export function resolveRunId(traceRunId: string | undefined): string {
  const runId = traceRunId ?? "run_pending";
  return /^run_[a-z0-9-]{1,40}$/.test(runId) ? runId : "run_pending";
}

const STAGE_FOR: Record<string, RuntimeEventV2["stage"]> = {
  "run.started": "run",
  "run.ended": "run",
  "session.started": "session",
  "session.ended": "session",
  "source.accepted": "source.accept",
  "source.rejected": "source.accept",
  "case.created": "case.create",
  "answer.committed": "episode.complete",
  "reflex.detected": "reflex.detect",
  "policy.evaluated": "policy.evaluate",
  "judgment.requested": "judgment.request",
  "judgment.completed": "judgment.response",
  "judgment.failed": "judgment.response",
  "model.requested": "model.request",
  "model.completed": "model.response",
  "model.failed": "model.response",
  "memory.stored": "memory.write",
  "episode.recorded": "episode.complete",
  "outcome.recorded": "episode.complete",
  "candidate.approved": "proposal.create",
  "candidate.rejected": "proposal.create",
  "review.created": "review.evaluate",
  "work.failed": "work",
};

export type TraceEmitInput = {
  type: string;
  stage?: RuntimeEventV2["stage"];
  status?: RuntimeEventV2["status"];
  reasonCode?: string;
  caseId?: string;
  reflexId?: string;
  judgmentId?: string;
  episodeId?: string;
  durationMs?: number;
  attempt?: number;
  selectedOutcome?: string;
};

/** Engine-facing trace API backed by RuntimeRecorder. */
export class EngineTrace {
  constructor(
    private readonly recorder: RuntimeRecorder,
    private readonly store: EngineStore,
  ) {}

  list(): readonly RuntimeEventV2[] {
    return this.recorder.list();
  }

  getLogError(): string | null {
    return this.recorder.getLogError();
  }

  async emit(partial: TraceEmitInput): Promise<void> {
    const stage = partial.stage ?? STAGE_FOR[partial.type] ?? "work";
    await this.recorder.emit({
      eventType: partial.type,
      stage,
      status: partial.status ?? "completed",
      ...(partial.caseId ? { caseId: partial.caseId } : {}),
      ...(partial.reflexId ? { reflexId: partial.reflexId } : {}),
      ...(partial.judgmentId ? { judgmentId: partial.judgmentId } : {}),
      ...(partial.episodeId ? { episodeId: partial.episodeId } : {}),
      ...(partial.reasonCode ? { reasonCode: knownReason(partial.reasonCode) } : {}),
      ...(partial.durationMs != null ? { durationMs: partial.durationMs } : {}),
      ...(partial.attempt != null ? { attempt: partial.attempt } : {}),
      queueDepth: await this.store.countWorkItems(),
    });
  }

  async note(eventType: string): Promise<void> {
    await this.emit({
      type: eventType === "source.rejected" ? "source.rejected" : "source.accepted",
      stage: "source.accept",
      status: eventType === "source.rejected" ? "failed" : "completed",
      reasonCode: eventType === "source.rejected" ? "listening_off" : "start",
    });
  }
}

export function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}
