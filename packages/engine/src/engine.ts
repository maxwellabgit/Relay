import type {
  ArtifactStorePort,
  GlassesDisplayPort,
  JudgmentPort,
  JudgmentRequest,
  ReflexModule,
  RelayChange,
  RelayCommand,
  RelayCommandResult,
  RelaySnapshot,
  TextModelPort,
  TranscriptSegmentV1,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { runJudgmentLifecycle } from "./judgment-lifecycle.js";
import { projectSnapshot } from "./projections.js";
import {
  definitionSearchTask,
  formatNoulInterval,
  noulConfidenceInterval,
  shouldCreateCaseForFinal,
} from "./policies.js";
import { PRIORITY_DIRECT, PRIORITY_OBSERVED, type WorkItem } from "./queue.js";
import { Scheduler, type Clock, type IdFactory } from "./scheduler.js";
import { DecisionLedger, type DecisionLogPort } from "./decision-ledger.js";
import type { EngineStore } from "./store.js";

export type EngineDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly model: TextModelPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly sessionId: string;
  readonly reflexModules?: readonly ReflexModule[];
  readonly glasses?: GlassesDisplayPort;
  /** Honest storage label for the developer panel. Defaults to memory. */
  readonly storageDetail?: string;
  /** Honest Jev label. Defaults to missing key until a TypeSafe key is configured. */
  readonly jevDetail?: string;
  readonly decisionLog?: DecisionLogPort;
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
  private readonly ledger: DecisionLedger;

  constructor(private readonly deps: EngineDeps) {
    this.scheduler = new Scheduler(deps.store, deps.clock, "engine");
    this.ledger = new DecisionLedger(
      deps.reflexModules?.length ?? 0,
      () => deps.clock.now().toISOString(),
      deps.decisionLog,
    );
  }

  async start(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.abort = new AbortController();
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.ensureSession(this.deps.sessionId, at);
    if (this.deps.decisionLog) {
      await this.ledger.hydrate(await this.deps.decisionLog.read());
    }
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
    const snapshot = await projectSnapshot(this.deps.store, this.deps.sessionId, this.statusChips());
    return { ...snapshot, ...this.ledger.view() };
  }

  async execute(command: RelayCommand): Promise<RelayCommandResult> {
    switch (command.type) {
      case "SetListening": {
        await this.deps.store.setListening(this.deps.sessionId, command.enabled);
        this.emit({ type: "ListeningChanged", listening: command.enabled });
        await this.note(
          "listening.changed",
          command.enabled ? "Listening on" : "Listening off",
        );
        await this.emitSnapshot();
        return { ok: true, summary: command.enabled ? "listening_on" : "listening_off" };
      }
      case "SubmitText": {
        const segment = this.makeTypedSegment(command.text);
        const caseId = await this.ingestFinalSegment(segment, true);
        return { ok: true, summary: "ask_accepted", caseId };
      }
      case "RememberToken":
        return this.rememberToken(command.token);
      case "StartWorkSession": {
        const started = await this.ledger.startSession();
        await this.emitSnapshot();
        return { ok: started === "started", summary: started };
      }
      case "EndWorkSession": {
        const ended = await this.ledger.endSession();
        await this.emitSnapshot();
        return { ok: ended === "completed", summary: ended };
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

    const listening = await this.deps.store.getListening(this.deps.sessionId);
    if (!isAsk && !listening) {
      await this.note("source.rejected", "Source rejected · listening off", {
        sourceEventId,
        segmentId: segment.segmentId,
        reasonCode: "listening_off",
      });
      await this.emitSnapshot();
      return "";
    }

    await this.note("source.accepted", `Source accepted · ${segment.origin}`, {
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
        text: segment.text,
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
            message: `Work failed · ${item.type}`,
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
    const text = String(item.payload.text ?? "");
    const isAsk = item.payload.isAsk === true;
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

    const findingSummaries: string[] = [];
    if (this.deps.reflexModules?.length) {
      const findings = await this.runReflexesOnFinal(
        text,
        isAsk,
        caseId,
        updated.version,
        sourceEventId,
      );
      findingSummaries.push(...findings);
    }

    const latest = await this.deps.store.getCase(caseId);
    if (!latest) return;
    const completed = await this.deps.store.updateCase(caseId, latest.version, {
      phase: "done",
      status: "completed",
      at: this.deps.clock.now().toISOString(),
    });
    if (!completed) return;

    if (current.origin === "direct") {
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "ask",
        summary: text,
        createdAt: completed.updatedAt,
        caseId,
      });
      if (findingSummaries.length > 0) {
        await this.deps.store.addFeedItem({
          itemId: this.deps.ids.next("feed"),
          kind: "answer",
          summary: findingSummaries.join(" · "),
          createdAt: this.deps.clock.now().toISOString(),
          caseId,
        });
      } else {
        const task = definitionSearchTask(text);
        if (task) {
          await this.deps.store.addFeedItem({
            itemId: this.deps.ids.next("feed"),
            kind: "task",
            summary: task,
            createdAt: this.deps.clock.now().toISOString(),
            caseId,
          });
        } else {
          const generated = await this.deps.model.generate(
            { taskKind: "direct_answer", prompt: text, caseId },
            this.abort?.signal ?? new AbortController().signal,
          );
          await this.deps.store.addFeedItem({
            itemId: this.deps.ids.next("feed"),
            kind: "answer",
            summary: generated.ok ? generated.text : "No local result for this Ask.",
            createdAt: this.deps.clock.now().toISOString(),
            caseId,
          });
        }
      }
    }

    const outcome = findingSummaries.length > 0 ? "answer" : definitionSearchTask(text) ? "task" : "no local result";
    await this.note(
      "feed.ready",
      current.origin === "direct" ? `Ask finished · ${outcome}` : `Observed speech finished · ${outcome}`,
    );
  }

  private async runReflexesOnFinal(
    text: string,
    isAsk: boolean,
    caseId: string,
    caseVersion: number,
    sourceEventId: string,
  ): Promise<string[]> {
    const findings: string[] = [];
    const sourceEvent = {
      sourceEventId,
      segmentId: sourceEventId,
      text,
      origin: isAsk ? "typed" : "scripted_transcript",
      speakerKey: null,
      startMs: 0,
      endMs: 0,
    };
    const detection = {
      sessionId: this.deps.sessionId,
      now: this.deps.clock.now().toISOString(),
      listening: await this.deps.store.getListening(this.deps.sessionId),
    };

    for (const reflex of this.deps.reflexModules ?? []) {
      const triggers = reflex.detect(sourceEvent, detection);
      for (const trigger of triggers) {
        const result = await reflex.evaluate({
          caseId,
          caseVersion,
          reflex: { id: reflex.definition.id, version: reflex.definition.version },
          triggerSourceRefs: [],
          eligibleConnections: [],
          remainingBudgets: reflex.definition.budgets,
          now: detection.now,
          observationText: text,
          triggerToken: trigger.token,
          isExplicitAsk: isAsk,
        });

        if (result.type === "finding") {
          findings.push(result.summary);
          if (!isAsk) {
            await this.deps.store.addFeedItem({
              itemId: this.deps.ids.next("feed"),
              kind: "finding",
              summary: result.summary,
              createdAt: this.deps.clock.now().toISOString(),
              caseId,
            });
          }
          const [title, ...rest] = result.summary.split(":");
          await this.deps.glasses?.show({
            kind: "finding",
            title: (title ?? "Finding").trim().slice(0, 40),
            body: rest.join(":").trim().slice(0, 192) || result.summary.slice(0, 192),
          });
          await this.note("reflex.finding", `Reflex finding · ${trigger.token}`, {
            caseId,
            caseVersion,
            sourceEventId,
            reflexId: reflex.definition.id,
            reflexVersion: reflex.definition.version,
            token: trigger.token,
            reasonCode: "finding",
          });
          await this.ledger.recordExactLookup(trigger.token);
        } else if (result.type === "clarification_required") {
          await this.ledger.recordJudgmentRequired(trigger.token);
          await this.note("reflex.no_action", `Reflex skipped · ${result.summary}`, {
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: result.summary,
          });
        } else {
          if (result.summary === "no_candidates") {
            await this.ledger.recordUnknownLookup(trigger.token);
          }
          await this.note("reflex.no_action", `Reflex skipped · ${result.summary}`, {
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: result.summary,
          });
        }
      }
    }
    return findings;
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
      { id: "jev" as const, label: "Jev", ok: false, detail: this.deps.jevDetail ?? "missing key" },
      { id: "model" as const, label: "Model", ok: false, detail: "disabled" },
      { id: "audio" as const, label: "Audio", ok: false, detail: "not connected" },
      { id: "halo" as const, label: "Halo", ok: false, detail: "offline" },
      {
        id: "storage" as const,
        label: "Storage",
        ok: true,
        detail: this.deps.storageDetail ?? "memory",
      },
    ];
  }

  private async rememberToken(rawToken: string): Promise<RelayCommandResult> {
    const token = rawToken.trim().toUpperCase();
    if (!/^[A-Z0-9]{2,12}$/.test(token)) {
      return { ok: false, summary: "invalid_token" };
    }

    await this.note("memory.requested", `Add to memory requested · ${token}`);
    const request: JudgmentRequest = {
      questionSetId: "judgment.remember",
      questionSetVersion: "1",
      model: "jev-1.13.0",
      provider: "typesafe",
      state: { token, proposal: "remember_acronym" },
      questions: {
        remember: {
          type: "noul",
          instructions:
            "Should this acronym be stored in the user's local memory? Answer yes only if it is a stable term worth recalling later.",
        },
      },
    };

    const signal = this.abort?.signal ?? new AbortController().signal;
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
      },
      request,
      signal,
    );

    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      const message = `Jev remember ${token} · ${category} · not accepted`;
      await this.note("jev.failed", message, { token, category });
      await this.ledger.recordRemember(token, { kind: "missing", category });
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "memory",
        summary: `Not stored · ${token} · Jev ${category}`,
        createdAt: this.deps.clock.now().toISOString(),
      });
      await this.emitSnapshot();
      return { ok: false, summary: category };
    }

    const answer = outcome.response.success.answers.remember;
    if (!answer || answer.type !== "noul") {
      await this.note("jev.failed", `Jev remember ${token} · invalid_response · not accepted`, {
        token,
      });
      await this.ledger.recordRemember(token, { kind: "missing", category: "invalid_response" });
      await this.emitSnapshot();
      return { ok: false, summary: "invalid_response" };
    }

    const interval = noulConfidenceInterval(answer.probabilityYes);
    if (!interval) {
      await this.note("jev.failed", `Jev remember ${token} · invalid probability · not accepted`, {
        token,
      });
      await this.ledger.recordRemember(token, { kind: "missing", category: "invalid_response" });
      await this.emitSnapshot();
      return { ok: false, summary: "invalid_response" };
    }

    const decision = `${formatNoulInterval(interval)} · ${interval.accept ? "accepted" : "not accepted"}`;
    await this.note(interval.accept ? "jev.accepted" : "jev.refused", `Jev remember ${token} · ${decision}`, {
      token,
      probabilityYes: interval.probabilityYes,
      confidence: interval.confidence,
      low: interval.low,
      high: interval.high,
    });
    await this.ledger.recordRemember(token, {
      kind: "noul",
      probabilityYes: interval.probabilityYes,
      accept: interval.accept,
    });

    if (!interval.accept) {
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "memory",
        summary: `Not stored · ${token} · ${formatNoulInterval(interval)}`,
        createdAt: this.deps.clock.now().toISOString(),
      });
      await this.emitSnapshot();
      return { ok: false, summary: "below_threshold" };
    }

    await this.note("memory.stored", `Memory stored · ${token}`, { token });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: `Remembered ${token} · ${formatNoulInterval(interval)}`,
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitSnapshot();
    return { ok: true, summary: "remembered" };
  }

  private async note(
    eventType: string,
    message: string,
    extra: Record<string, unknown> = {},
  ): Promise<void> {
    const at = this.deps.clock.now().toISOString();
    const sequence = await this.deps.store.appendDomainEvent(eventType, at, {
      message,
      ...extra,
    });
    this.emit({ type: "TraceAppended", sequence, eventType, message, at });
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
