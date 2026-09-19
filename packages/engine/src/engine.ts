import type {
  ArtifactStorePort,
  JudgmentPort,
  RelayChange,
  RelayCommand,
  RelayCommandResult,
  RelaySnapshot,
  TextModelPort,
  TranscriptSegmentV1,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { projectSnapshot } from "./projections.js";
import { shouldCreateCaseForFinal } from "./policies.js";
import { PRIORITY_DIRECT, PRIORITY_OBSERVED, type WorkItem } from "./queue.js";
import { Scheduler, type Clock, type IdFactory } from "./scheduler.js";
import type { EngineStore } from "./store.js";

export type EngineDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly model: TextModelPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly sessionId: string;
  readonly reflexes?: {
    onSourceFinal(input: {
      sourceEventId: string;
      segment: TranscriptSegmentV1;
      caseId: string;
      caseVersion: number;
    }): Promise<void>;
  };
};

function encodeText(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export class RelayEngine {
  private readonly scheduler: Scheduler;
  private readonly listeners = new Set<(change: RelayChange) => void>();
  private running = false;
  private loopPromise: Promise<void> | null = null;
  private abort: AbortController | null = null;

  constructor(private readonly deps: EngineDeps) {
    this.scheduler = new Scheduler(deps.store, deps.clock, "engine");
  }

  async start(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.abort = new AbortController();
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.ensureSession(this.deps.sessionId, at);
    this.loopPromise = this.runLoop(this.abort.signal);
    await this.emitSnapshot();
  }

  async stop(): Promise<void> {
    this.running = false;
    this.abort?.abort();
    await this.loopPromise;
    this.loopPromise = null;
  }

  subscribe(listener: (change: RelayChange) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async getSnapshot(): Promise<RelaySnapshot> {
    return projectSnapshot(this.deps.store, this.deps.sessionId, this.statusChips());
  }

  async execute(command: RelayCommand): Promise<RelayCommandResult> {
    switch (command.type) {
      case "SetListening": {
        await this.deps.store.setListening(this.deps.sessionId, command.enabled);
        this.emit({ type: "ListeningChanged", listening: command.enabled });
        await this.emitSnapshot();
        return { ok: true, summary: command.enabled ? "listening_on" : "listening_off" };
      }
      case "SubmitText": {
        const segment = this.makeTypedSegment(command.text);
        const caseId = await this.ingestFinalSegment(segment, true);
        return { ok: true, summary: "ask_accepted", caseId };
      }
      default:
        return { ok: false, summary: "unsupported_command", error: command.type };
    }
  }

  /** Test/helper: inject a final transcript segment as if a source emitted it. */
  async ingestFinalSegment(segment: TranscriptSegmentV1, isAsk: boolean): Promise<string> {
    const bytes = encodeText(segment.text);
    const sha256 = await sha256Hex(bytes);
    const artifact = await this.deps.artifacts.put(bytes, localOnlyPolicy());
    const sourceEventId = this.deps.ids.next("src");
    const at = this.deps.clock.now().toISOString();

    const { inserted } = await this.deps.store.persistFinalSource({
      sourceEventId,
      sessionId: segment.sessionId,
      segment,
      textArtifactId: artifact.artifactId,
      textSha256: sha256,
      policy: artifact.policy,
      createdAt: at,
    });

    if (!inserted) {
      return "";
    }

    await this.deps.store.appendDomainEvent("source.final", at, {
      sourceEventId,
      segmentId: segment.segmentId,
    });

    const routing = shouldCreateCaseForFinal(segment.origin, isAsk);
    const caseId = this.deps.ids.next("case");
    const record = await this.deps.store.createCase({
      caseId,
      origin: routing.origin,
      kind: routing.kind,
      priority: routing.priority,
      at,
    });

    await this.scheduler.enqueue(
      "source.final",
      {
        sourceEventId,
        caseId: record.caseId,
        caseVersion: record.version,
        segmentId: segment.segmentId,
        isAsk,
      },
      routing.priority,
      this.deps.ids,
    );

    await this.emitSnapshot();
    return record.caseId;
  }

  private makeTypedSegment(text: string): TranscriptSegmentV1 {
    const now = this.deps.clock.now().getTime();
    return {
      schemaVersion: 1,
      sourceId: "manual",
      sessionId: this.deps.sessionId,
      segmentId: this.deps.ids.next("seg"),
      revision: 1,
      sequence: now,
      startMs: now,
      endMs: now,
      speakerKey: null,
      speakerConfidence: null,
      text,
      textConfidence: 1,
      final: true,
      origin: "typed",
      cursor: null,
    };
  }

  private async runLoop(signal: AbortSignal): Promise<void> {
    while (!signal.aborted && this.running) {
      const item = await this.scheduler.claim();
      if (!item) {
        await sleep(25, signal);
        continue;
      }
      try {
        await this.process(item);
        await this.scheduler.complete(item.workId);
        await this.emitSnapshot();
      } catch (error) {
        await this.deps.store.appendDomainEvent(
          "work.failed",
          this.deps.clock.now().toISOString(),
          {
            workId: item.workId,
            type: item.type,
            error: error instanceof Error ? error.message : "unknown",
          },
        );
        await this.scheduler.complete(item.workId);
      }
    }
  }

  private async process(item: WorkItem): Promise<void> {
    switch (item.type) {
      case "source.final":
        await this.onSourceFinal(item);
        return;
      case "case.resume":
        await this.onCaseResume(item);
        return;
      case "judgment.completed":
      case "model.completed":
      case "operation.completed":
        await this.onCompletion(item);
        return;
      case "timer.due":
        await this.onTimer(item);
        return;
    }
  }

  private async onSourceFinal(item: WorkItem): Promise<void> {
    const caseId = String(item.payload.caseId);
    const caseVersion = Number(item.payload.caseVersion);
    const sourceEventId = String(item.payload.sourceEventId);
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.version !== caseVersion) return;

    const updated = await this.deps.store.updateCase(caseId, caseVersion, {
      phase: "detect",
      status: "active",
      at: this.deps.clock.now().toISOString(),
    });
    if (!updated) return;

    await this.deps.store.appendCaseEvent(caseId, updated.version, "case.phase_changed", updated.updatedAt, {
      phase: "detect",
      sourceEventId,
    });

    if (this.deps.reflexes) {
      // Reflex wiring lands in commit 5; hook is present so the loop stays autonomous.
      await this.deps.store.appendCaseEvent(
        caseId,
        updated.version,
        "case.phase_changed",
        this.deps.clock.now().toISOString(),
        { phase: "publish", note: "reflex_hook_pending" },
      );
    }

    const completed = await this.deps.store.updateCase(caseId, updated.version, {
      phase: "done",
      status: "completed",
      at: this.deps.clock.now().toISOString(),
    });
    if (!completed) return;

    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: current.origin === "direct" ? "ask" : "observation",
      summary:
        current.origin === "direct"
          ? "Ask accepted"
          : `Observed source ${String(item.payload.segmentId)}`,
      createdAt: completed.updatedAt,
      caseId,
    });
  }

  private async onCaseResume(item: WorkItem): Promise<void> {
    const caseId = String(item.payload.caseId);
    const expectedVersion = Number(item.payload.caseVersion);
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.version !== expectedVersion) return;
    await this.deps.store.updateCase(caseId, expectedVersion, {
      status: "active",
      waitKind: null,
      phase: "detect",
      at: this.deps.clock.now().toISOString(),
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
    await this.scheduler.enqueue(
      "case.resume",
      { caseId, caseVersion: expectedVersion },
      PRIORITY_OBSERVED,
      this.deps.ids,
    );
  }

  private statusChips() {
    return [
      { id: "engine" as const, label: "Engine", ok: this.running, detail: this.running ? "running" : "stopped" },
      { id: "jev" as const, label: "Jev", ok: true, detail: "recorded" },
      { id: "model" as const, label: "Model", ok: true, detail: "ready" },
      { id: "audio" as const, label: "Audio", ok: true, detail: "idle" },
      { id: "halo" as const, label: "Halo", ok: true, detail: "offline" },
      { id: "storage" as const, label: "Storage", ok: true, detail: "sqlite" },
    ];
  }

  private emit(change: RelayChange): void {
    for (const listener of this.listeners) listener(change);
  }

  private async emitSnapshot(): Promise<void> {
    const snapshot = await this.getSnapshot();
    this.emit({ type: "SnapshotReplaced", snapshot });
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
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

export { PRIORITY_DIRECT, PRIORITY_OBSERVED };
