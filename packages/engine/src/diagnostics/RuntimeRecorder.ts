import type { RunManifestV1 } from "@relay/contracts";
import { isRuntimeEvent, knownReason, type RuntimeEventV2 } from "../runtime-events.js";
import type { TraceSink } from "../trace-sink.js";

export type RuntimeRecorderDeps = {
  readonly sink?: TraceSink;
  readonly clock: { now: () => Date };
  readonly runId: string;
  readonly onEvent?: (event: RuntimeEventV2) => void;
  readonly onError?: (code: string) => void;
};

export class RuntimeRecorder {
  private sequence = 0;
  private readonly events: RuntimeEventV2[] = [];
  private logError: string | null = null;

  constructor(private readonly deps: RuntimeRecorderDeps) {}

  getLogError(): string | null {
    return this.logError;
  }

  list(): readonly RuntimeEventV2[] {
    return this.events;
  }

  /** Hydrate from durable sink (e.g. on engine start). Continues sequence from loaded events. */
  hydrate(events: readonly RuntimeEventV2[], logError?: string | null): void {
    this.events.length = 0;
    this.events.push(...events);
    this.sequence = events.reduce((max, event) => Math.max(max, event.sequence), 0);
    if (logError !== undefined) this.logError = logError;
  }

  markSinkMissing(): void {
    this.logError = "trace_sink_missing";
    this.deps.onError?.("trace_sink_missing");
  }

  async emit(partial: {
    eventType: string;
    stage: RuntimeEventV2["stage"];
    status?: RuntimeEventV2["status"];
    reasonCode?: string;
    sessionId?: string;
    episodeId?: string;
    caseId?: string;
    workId?: string;
    judgmentId?: string;
    receiptId?: string;
    reflexId?: string;
    durationMs?: number;
    attempt?: number;
    queueDepth?: number;
    toolId?: string;
    providerRequestId?: string;
    httpStatus?: number;
    disclosureGrantId?: string;
    retryDelayMs?: number;
    grantScopeKind?: "session" | "project";
    grantExpiresAt?: string;
    grantRequestsBefore?: number;
    grantRequestsAfter?: number;
    grantBytesBefore?: number;
    grantBytesAfter?: number;
    grantMaxRequests?: number;
    grantMaxBytes?: number;
    disclosedSourceCount?: number;
    disclosedBytes?: number;
  }): Promise<RuntimeEventV2> {
    const event: RuntimeEventV2 = {
      schemaVersion: 2,
      sequence: ++this.sequence,
      runId: this.deps.runId,
      at: this.deps.clock.now().toISOString(),
      eventType: partial.eventType,
      stage: partial.stage,
      status: partial.status ?? "completed",
      ...(partial.sessionId ? { sessionId: partial.sessionId } : {}),
      ...(partial.episodeId ? { episodeId: partial.episodeId } : {}),
      ...(partial.caseId ? { caseId: partial.caseId } : {}),
      ...(partial.workId ? { workId: partial.workId } : {}),
      ...(partial.judgmentId ? { judgmentId: partial.judgmentId } : {}),
      ...(partial.receiptId ? { receiptId: partial.receiptId } : {}),
      ...(partial.reflexId ? { reflexId: partial.reflexId } : {}),
      ...(partial.reasonCode ? { reasonCode: knownReason(partial.reasonCode) } : {}),
      ...(partial.durationMs != null ? { durationMs: partial.durationMs } : {}),
      ...(partial.attempt != null ? { attempt: partial.attempt } : {}),
      ...(partial.queueDepth != null ? { queueDepth: partial.queueDepth } : {}),
      ...(partial.toolId ? { toolId: partial.toolId } : {}),
      ...(partial.providerRequestId ? { providerRequestId: partial.providerRequestId } : {}),
      ...(partial.httpStatus != null ? { httpStatus: partial.httpStatus } : {}),
      ...(partial.disclosureGrantId ? { disclosureGrantId: partial.disclosureGrantId } : {}),
      ...(partial.retryDelayMs != null ? { retryDelayMs: partial.retryDelayMs } : {}),
      ...(partial.grantScopeKind ? { grantScopeKind: partial.grantScopeKind } : {}),
      ...(partial.grantExpiresAt ? { grantExpiresAt: partial.grantExpiresAt } : {}),
      ...(partial.grantRequestsBefore != null ? { grantRequestsBefore: partial.grantRequestsBefore } : {}),
      ...(partial.grantRequestsAfter != null ? { grantRequestsAfter: partial.grantRequestsAfter } : {}),
      ...(partial.grantBytesBefore != null ? { grantBytesBefore: partial.grantBytesBefore } : {}),
      ...(partial.grantBytesAfter != null ? { grantBytesAfter: partial.grantBytesAfter } : {}),
      ...(partial.grantMaxRequests != null ? { grantMaxRequests: partial.grantMaxRequests } : {}),
      ...(partial.grantMaxBytes != null ? { grantMaxBytes: partial.grantMaxBytes } : {}),
      ...(partial.disclosedSourceCount != null ? { disclosedSourceCount: partial.disclosedSourceCount } : {}),
      ...(partial.disclosedBytes != null ? { disclosedBytes: partial.disclosedBytes } : {}),
    };
    if (!isRuntimeEvent(event)) {
      this.logError = "trace_rejected";
      this.deps.onError?.("trace_rejected");
      throw new Error("trace_rejected");
    }
    this.events.push(event);
    this.deps.onEvent?.(event);
    if (!this.deps.sink) {
      this.logError = "trace_sink_missing";
      this.deps.onError?.("trace_sink_missing");
      return event;
    }
    try {
      await this.deps.sink.append(event);
      this.logError = null;
    } catch (error) {
      this.logError = error instanceof Error ? error.message : "trace_write_failed";
      this.deps.onError?.(this.logError);
    }
    return event;
  }

  async span<T>(
    input: {
      eventType: string;
      stage: RuntimeEventV2["stage"];
      reasonCode?: string;
      caseId?: string;
      workId?: string;
      judgmentId?: string;
      reflexId?: string;
      episodeId?: string;
      attempt?: number;
      queueDepth?: number;
    },
    work: () => Promise<T>,
  ): Promise<T> {
    const started = this.deps.clock.now().getTime();
    await this.emit({
      ...input,
      status: "started",
    });
    try {
      const result = await work();
      await this.emit({
        ...input,
        status: "completed",
        durationMs: Math.max(0, this.deps.clock.now().getTime() - started),
      });
      return result;
    } catch (error) {
      await this.emit({
        ...input,
        status: "failed",
        reasonCode: input.reasonCode ?? "work_failed",
        durationMs: Math.max(0, this.deps.clock.now().getTime() - started),
      });
      throw error;
    }
  }
}

export function buildRunManifest(input: {
  readonly runId: string;
  readonly gitCommit: string;
  readonly appVersion?: string;
  readonly reflexVersions?: Readonly<Record<string, number>>;
  readonly policyHashes?: Readonly<Record<string, string>>;
  readonly fixtureHashes?: Readonly<Record<string, string>>;
  readonly providerModes?: readonly string[];
  readonly startedAt: string;
}): RunManifestV1 {
  return {
    schemaVersion: 1,
    runId: input.runId,
    gitCommit: input.gitCommit,
    gitDirty: false,
    appVersion: input.appVersion ?? "0.1.0",
    protocolVersion: "2",
    os: typeof process !== "undefined" ? process.platform : "unknown",
    fixtureHashes: input.fixtureHashes ?? {},
    modelNames: input.providerModes ?? [],
    reflexVersions: input.reflexVersions ?? {},
    policyHashes: input.policyHashes ?? {},
    startedAt: input.startedAt,
  };
}
