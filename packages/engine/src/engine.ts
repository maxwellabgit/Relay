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
  TraceEventV1,
  TranscriptSegmentV1,
} from "@relay/contracts";
import { isRetryableJudgmentFailure, localOnlyPolicy } from "@relay/contracts";
import { inspectRuntime } from "./inspect.js";
import { runJudgmentLifecycle } from "./judgment-lifecycle.js";
import {
  BENEFIT_YES_MINIMUM,
  calendarRecommendation,
  foldPattern,
  patternReady,
  workSignature,
  type ReceiptRecord,
} from "./learning-store.js";
import {
  askedToken,
  definitionSearchTask,
  evaluateChoiceGate,
  shouldCreateCaseForFinal,
  validateBirthday,
} from "./policies.js";
import { projectSnapshot } from "./projections.js";
import { PRIORITY_DIRECT, PRIORITY_OBSERVED, type WorkItem } from "./queue.js";
import { Scheduler, type Clock, type IdFactory } from "./scheduler.js";
import type { EngineStore } from "./store.js";
import type { TraceSink } from "./trace-sink.js";

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
  readonly storageDetail?: string;
  readonly jevStatus?: { readonly ok: boolean; readonly detail: string };
  readonly modelStatus?: { readonly ok: boolean; readonly detail: string };
  readonly mode?: "live" | "recorded" | "replay";
  readonly gitCommit?: string;
  readonly trace?: TraceSink;
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
  private traces: TraceEventV1[] = [];
  private logError: string | null = null;
  private disposition: "complete" | "retry" | "dead" = "complete";
  private dispositionReason = "completed";

  constructor(private readonly deps: EngineDeps) {
    this.scheduler = new Scheduler(deps.store, deps.clock, "engine");
  }

  async start(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.abort = new AbortController();
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.ensureSession(this.deps.sessionId, at);
    await this.deps.store.learning.compact(at);
    if (this.deps.trace) {
      this.traces = [...(await this.deps.trace.read())];
    } else {
      this.logError = "trace_sink_missing";
    }
    this.loopPromise = this.runLoop(this.abort.signal);
    await this.emitTrace({ type: "run.started", reasonCode: "start" });
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
    const inspected = await inspectRuntime(this.deps.store.learning, this.traces, {
      runId: this.deps.trace?.runId ?? "no-run",
      commit: this.deps.gitCommit ?? "unknown",
      queueDepth: snapshot.queueDepth,
      logPath: this.deps.trace?.directoryLabel ?? "",
      logWritable: this.logError === null && this.deps.trace != null,
      logError: this.logError,
      mode: this.deps.mode ?? "live",
      retention: "7d",
    });
    return { ...snapshot, ...inspected };
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
      case "CaptureBirthday":
        return this.captureBirthday(command.personKey, command.date, command.confirmed);
      case "RecordCompletedWork":
        return this.recordCompletedWork(command.fields);
      case "ApproveCandidate":
        return this.approveCandidate(command.candidateId);
      case "StartWorkSession":
        return this.startWorkSession();
      case "EndWorkSession":
        return this.endWorkSession();
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
        this.disposition = "complete";
        this.dispositionReason = "completed";
        const disposition = await this.process(item);
        if (disposition === "retry") {
          const available = new Date(this.deps.clock.now().getTime() + 1000).toISOString();
          await this.deps.store.requeue(item.workId, available);
        } else if (disposition === "dead") {
          await this.deps.store.deadLetter(
            item.workId,
            this.dispositionReason,
            this.deps.clock.now().toISOString(),
          );
        } else {
          await this.scheduler.complete(item.workId);
        }
        await this.emitSnapshot();
      } catch {
        await this.deps.store.deadLetter(item.workId, "work_failed", this.deps.clock.now().toISOString());
        await this.emitTrace({ type: "work.failed", reasonCode: "work_failed" });
      }
    }
  }

  private async process(item: WorkItem): Promise<"complete" | "retry" | "dead"> {
    switch (item.type) {
      case "source.final":
        await this.onSourceFinal(item);
        return this.disposition;
      case "case.resume":
        await this.onCaseResume(item);
        return this.disposition;
      case "judgment.completed":
      case "model.completed":
      case "operation.completed":
        await this.onCompletion(item);
        return this.disposition;
      case "timer.due":
        await this.onTimer(item);
        return this.disposition;
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

    const reflex = await this.runReflexesOnFinal(text, isAsk, caseId, updated.version, sourceEventId);
    if (reflex.blocked) {
      await this.deps.store.updateCase(caseId, updated.version, {
        phase: "judge",
        status: "waiting",
        waitKind: "judgment",
        at: this.deps.clock.now().toISOString(),
      });
      this.disposition = reflex.blocked.retry ? "retry" : "dead";
      this.dispositionReason = reflex.blocked.reason;
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "wait",
        summary: `Jev choice unavailable · ${reflex.blocked.reason}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.emitTrace({
        type: "judgment.failed",
        caseId,
        reasonCode: reflex.blocked.reason,
        waitState: reflex.blocked.retry ? "retry" : "dead_letter",
      });
      return;
    }

    const findingSummaries = [...reflex.findings];
    const token = askedToken(text);
    let signature = reflex.signature;
    if (findingSummaries.length === 0 && token) {
      const memory = await this.deps.store.learning.getMemory("glossary", token);
      if (memory) {
        const expansion = memory.value.expansion ?? "";
        findingSummaries.push(
          expansion ? `${token}: ${expansion}` : `${token} is saved locally. No expansion is stored.`,
        );
        signature = workSignature("acronym.lookup", { outcome: "memory", token });
        await this.putReceipt({
          caseId,
          gateId: "glossary.memory",
          policyVersion: "memory@1",
          questionType: "not_applicable",
          provider: "not_applicable",
          probabilities: {},
          thresholds: {},
          selectedOption: token,
          result: "not_applicable",
          reasonCode: "explicit_memory",
          latencyMs: null,
        });
      }
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
        if (task && token && !reflex.suppressSearch) {
          signature = workSignature("acronym.lookup", { outcome: "no_candidates", token });
          await this.putReceipt({
            caseId,
            gateId: "reflex.resolve-acronym",
            policyVersion: "resolve-acronym@1",
            questionType: "not_applicable",
            provider: "not_applicable",
            probabilities: {},
            thresholds: {},
            selectedOption: null,
            result: "not_applicable",
            reasonCode: "no_candidates",
            latencyMs: null,
          });
          await this.deps.store.addFeedItem({
            itemId: this.deps.ids.next("feed"),
            kind: "task",
            summary: task,
            createdAt: this.deps.clock.now().toISOString(),
            caseId,
          });
        } else if (task) {
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

    if (signature) {
      await this.recordEpisode(signature, caseId, "completed");
    }
    await this.emitTrace({
      type: "outcome.completed",
      caseId,
      reasonCode: signature ? "episode_completed" : "no_episode",
      selectedOutcome: signature ? "completed" : "none",
    });
  }

  private async runReflexesOnFinal(
    text: string,
    isAsk: boolean,
    caseId: string,
    caseVersion: number,
    sourceEventId: string,
  ): Promise<{
    findings: string[];
    signature: string | null;
    blocked: { retry: boolean; reason: string } | null;
    suppressSearch: boolean;
  }> {
    const findings: string[] = [];
    let signature: string | null = null;
    let suppressSearch = false;
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
      await this.emitTrace({
        type: "reflex.detected",
        caseId,
        reflexId: reflex.definition.id,
        reflexVersion: reflex.definition.version,
        reasonCode: "detected",
        selectedOutcome: String(triggers.length),
      });
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
          signature = workSignature("acronym.lookup", { outcome: "exact", token: trigger.token });
          await this.putReceipt({
            caseId,
            gateId: reflex.definition.id,
            policyVersion: `${reflex.definition.id}@${reflex.definition.version}`,
            questionType: "not_applicable",
            provider: "not_applicable",
            probabilities: {},
            thresholds: {},
            selectedOption: trigger.token,
            result: "not_applicable",
            reasonCode: "exact_glossary",
            latencyMs: null,
          });
          await this.emitTrace({
            type: "policy.evaluated",
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: "exact_glossary",
            selectedOutcome: "not_applicable",
          });
        } else if (result.type === "clarification_required") {
          const choice = await this.resolveChoice(result.clarificationPrompt ?? "", caseId, reflex.definition.id);
          if (choice.blocked) return { findings, signature, blocked: choice.blocked, suppressSearch: true };
          suppressSearch = true;
          if (choice.finding) {
            findings.push(choice.finding);
            signature = workSignature("acronym.lookup", { outcome: "choice", token: trigger.token });
          }
        } else if (result.summary === "no_candidates") {
          signature = workSignature("acronym.lookup", { outcome: "no_candidates", token: trigger.token });
          await this.emitTrace({
            type: "policy.evaluated",
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: "no_candidates",
            selectedOutcome: "not_applicable",
          });
        }
      }
    }
    return { findings, signature, blocked: null, suppressSearch };
  }

  private async resolveChoice(
    prompt: string,
    caseId: string,
    reflexId: string,
  ): Promise<{ finding: string | null; blocked: { retry: boolean; reason: string } | null }> {
    let parsed: { optionIds?: string[]; choiceProbabilityMinimum?: number; choiceMarginMinimum?: number } = {};
    try {
      parsed = JSON.parse(prompt) as typeof parsed;
    } catch {
      return { finding: null, blocked: { retry: false, reason: "invalid_gate" } };
    }
    const optionIds = parsed.optionIds ?? [];
    const minimum = parsed.choiceProbabilityMinimum ?? 0.65;
    const marginMinimum = parsed.choiceMarginMinimum ?? 0.15;
    const criteria: Record<string, string> = {};
    for (const option of optionIds) criteria[option] = option;
    criteria.no_match = "No matching expansion";
    const request: JudgmentRequest = {
      questionSetId: "judgment.acronym-choice",
      questionSetVersion: "1",
      model: "jev-1.13.0",
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: { optionCount: optionIds.length },
      questions: {
        expansion: { type: "choice", instructions: "Select one allowed option.", criteria, requireNoMatch: true },
      },
      caseId,
    };
    const started = Date.now();
    await this.emitTrace({ type: "judgment.requested", caseId, reflexId, reasonCode: "choice" });
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
      },
      request,
      this.abort?.signal ?? new AbortController().signal,
    );
    const latencyMs = Date.now() - started;
    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      await this.putReceipt({
        caseId,
        gateId: reflexId,
        policyVersion: "resolve-acronym@1",
        questionType: "choice",
        provider: request.provider ?? "unknown",
        probabilities: {},
        thresholds: { choiceProbabilityMinimum: minimum, choiceMarginMinimum: marginMinimum },
        selectedOption: null,
        result: "wait",
        reasonCode: category,
        latencyMs,
      });
      return {
        finding: null,
        blocked: { retry: isRetryableJudgmentFailure(category), reason: category },
      };
    }
    const answer = outcome.response.success.answers.expansion;
    if (!answer || answer.type !== "choice") {
      await this.putReceipt({
        caseId,
        gateId: reflexId,
        policyVersion: "resolve-acronym@1",
        questionType: "choice",
        provider: request.provider ?? "unknown",
        probabilities: {},
        thresholds: { choiceProbabilityMinimum: minimum },
        selectedOption: null,
        result: "fail",
        reasonCode: "invalid_response",
        latencyMs,
      });
      return { finding: null, blocked: { retry: false, reason: "invalid_response" } };
    }
    const gate = evaluateChoiceGate({
      probabilities: answer.probabilities,
      minimum,
      marginMinimum,
    });
    const allowed = gate.selected === "no_match" || optionIds.includes(gate.selected);
    const pass = gate.pass && allowed;
    const reasonCode = pass ? "policy_pass" : allowed ? gate.reasonCode : "not_in_options";
    await this.putReceipt({
      caseId,
      gateId: reflexId,
      policyVersion: "resolve-acronym@1",
      questionType: "choice",
      provider: request.provider ?? "unknown",
      probabilities: answer.probabilities,
      thresholds: { choiceProbabilityMinimum: minimum, choiceMarginMinimum: marginMinimum },
      selectedOption: gate.selected,
      result: pass ? "pass" : "fail",
      reasonCode,
      latencyMs,
    });
    await this.emitTrace({
      type: "judgment.completed",
      caseId,
      reflexId,
      judgmentId: outcome.record.judgmentId,
      probabilities: answer.probabilities,
      thresholds: { choiceProbabilityMinimum: minimum, choiceMarginMinimum: marginMinimum },
      selectedOutcome: pass ? "pass" : "fail",
      reasonCode,
      latencyMs,
    });
    if (!pass || gate.selected === "no_match") return { finding: null, blocked: null };
    return { finding: gate.selected, blocked: null };
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
    const jev = this.deps.jevStatus ?? { ok: false, detail: "missing key" };
    const model = this.deps.modelStatus ?? { ok: false, detail: "disabled" };
    return [
      { id: "engine" as const, label: "Engine", ok: this.running, detail: this.running ? "running" : "stopped" },
      { id: "jev" as const, label: "Jev", ok: jev.ok, detail: jev.detail },
      { id: "model" as const, label: "Model", ok: model.ok, detail: model.detail },
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
    if (!/^[A-Z0-9]{2,12}$/.test(token)) return { ok: false, summary: "invalid_token" };
    const existing = await this.deps.store.learning.getMemory("glossary", token);
    await this.deps.store.learning.putMemory({
      memoryId: existing?.memoryId ?? this.deps.ids.next("mem"),
      kind: "glossary",
      key: token,
      value: { expansion: existing?.value.expansion ?? "" },
      source: "explicit_user",
      createdAt: existing?.createdAt ?? this.deps.clock.now().toISOString(),
    });
    await this.putReceipt({
      caseId: null,
      gateId: "memory.explicit",
      policyVersion: "memory@1",
      questionType: "user",
      provider: "user",
      probabilities: {},
      thresholds: {},
      selectedOption: token,
      result: "pass",
      reasonCode: "explicit_user",
      latencyMs: null,
    });
    await this.emitTrace({ type: "memory.stored", reasonCode: "explicit_user", selectedOutcome: token });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: `Remembered ${token}`,
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitSnapshot();
    return { ok: true, summary: "remembered" };
  }

  private async captureBirthday(
    personKey: string,
    date: string,
    confirmed: boolean,
  ): Promise<RelayCommandResult> {
    const invalid = validateBirthday(personKey, date);
    if (invalid) return { ok: false, summary: invalid };
    if (!confirmed) {
      await this.putReceipt({
        caseId: null,
        gateId: "birthday.confirm",
        policyVersion: "memory@1",
        questionType: "user",
        provider: "user",
        probabilities: {},
        thresholds: {},
        selectedOption: null,
        result: "wait",
        reasonCode: "confirmation_required",
        latencyMs: null,
      });
      await this.emitSnapshot();
      return { ok: false, summary: "confirmation_required" };
    }
    await this.deps.store.learning.putMemory({
      memoryId: this.deps.ids.next("mem"),
      kind: "birthday",
      key: personKey,
      value: { date },
      source: "explicit_user",
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitTrace({ type: "memory.stored", reasonCode: "birthday_confirmed", selectedOutcome: "birthday" });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: `Birthday saved · ${personKey}`,
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitSnapshot();
    return { ok: true, summary: "birthday_stored" };
  }

  private async recordCompletedWork(fields: {
    start_bucket: string;
    duration: string;
    reminder_offset: string;
  }): Promise<RelayCommandResult> {
    const signature = workSignature("calendar.block", fields);
    if (!signature) return { ok: false, summary: "invalid_signature" };
    await this.recordEpisode(signature, null, "completed");
    await this.emitSnapshot();
    return { ok: true, summary: "episode_recorded" };
  }

  private async approveCandidate(candidateId: string): Promise<RelayCommandResult> {
    const candidates = await this.deps.store.learning.listCandidates();
    const current = candidates.find((candidate) => candidate.candidateId === candidateId);
    if (!current || current.state !== "proposed") return { ok: false, summary: "not_proposed" };
    await this.deps.store.learning.putCandidate({
      ...current,
      state: "approved",
      needed: "",
      updatedAt: this.deps.clock.now().toISOString(),
    });
    await this.emitTrace({ type: "candidate.approved", reasonCode: "user_approval", selectedOutcome: "approved" });
    await this.emitSnapshot();
    return { ok: true, summary: "approved" };
  }

  private async startWorkSession(): Promise<RelayCommandResult> {
    const open = await this.deps.store.learning.currentSession();
    if (open) return { ok: false, summary: "already_open" };
    await this.openWorkSession();
    await this.emitSnapshot();
    return { ok: true, summary: "started" };
  }

  private async endWorkSession(): Promise<RelayCommandResult> {
    const open = await this.deps.store.learning.currentSession();
    if (!open) return { ok: false, summary: "no_session" };
    const episodes = (await this.deps.store.learning.listEpisodes()).filter(
      (episode) => episode.sessionId === open.sessionId,
    );
    const termination = episodes.length === 0 ? "abandoned" : "completed";
    await this.deps.store.learning.closeSession(
      open.sessionId,
      this.deps.clock.now().toISOString(),
      termination,
      episodes.length,
    );
    await this.deps.store.learning.compact(this.deps.clock.now().toISOString());
    await this.maybeReview();
    await this.emitTrace({
      type: "session.ended",
      reasonCode: termination,
      selectedOutcome: String(episodes.length),
    });
    await this.emitSnapshot();
    return { ok: termination === "completed", summary: termination };
  }

  private async openWorkSession(): Promise<string> {
    const sessionId = this.deps.ids.next("work");
    await this.deps.store.learning.openSession({
      sessionId,
      startedAt: this.deps.clock.now().toISOString(),
      endedAt: null,
      termination: "open",
      episodeCount: 0,
    });
    await this.emitTrace({ type: "session.started", reasonCode: "open", selectedOutcome: sessionId });
    return sessionId;
  }

  private async recordEpisode(
    signature: string,
    caseId: string | null,
    outcome: "completed" | "abandoned",
  ): Promise<void> {
    const open = (await this.deps.store.learning.currentSession()) ?? {
      sessionId: await this.openWorkSession(),
    };
    const episodeId = this.deps.ids.next("episode");
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.learning.putEpisode({
      episodeId,
      sessionId: open.sessionId,
      caseId,
      signature,
      outcome,
      startedAt: at,
      completedAt: at,
    });
    const existing = await this.deps.store.learning.getPattern(signature);
    const episode = {
      episodeId,
      sessionId: open.sessionId,
      caseId,
      signature,
      outcome,
      startedAt: at,
      completedAt: at,
    };
    const pattern = foldPattern(existing, episode);
    await this.deps.store.learning.putPattern(pattern);
    if (patternReady(pattern)) await this.considerCandidate(pattern.signature, pattern.count, pattern.sessionIds.length);
    await this.emitTrace({
      type: "episode.completed",
      ...(caseId ? { caseId } : {}),
      reasonCode: "completed",
      selectedOutcome: signature,
    });
  }

  private async considerCandidate(signature: string, count: number, sessions: number): Promise<void> {
    const candidateId = `cand_${signature}`;
    const existing = (await this.deps.store.learning.listCandidates()).find(
      (candidate) => candidate.candidateId === candidateId,
    );
    if (existing && (existing.state === "proposed" || existing.state === "approved" || existing.state === "active")) {
      return;
    }
    const pattern = await this.deps.store.learning.getPattern(signature);
    const because = pattern ? (calendarRecommendation(pattern) ?? "") : "";
    const request: JudgmentRequest = {
      questionSetId: "judgment.expansion-benefit",
      questionSetVersion: "1",
      model: "jev-1.13.0",
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: { count, sessions },
      questions: {
        benefit: { type: "noul", instructions: "Is this repeated work stable enough to offer as a capability?" },
      },
    };
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
      },
      request,
      this.abort?.signal ?? new AbortController().signal,
    );
    let state: "candidate" | "proposed" = "candidate";
    let needed = "jev_benefit_noul";
    let result: ReceiptRecord["result"] = "wait";
    let reason = "jev_unavailable";
    let probability = 0;
    if (outcome.response.ok) {
      const answer = outcome.response.success.answers.benefit;
      if (answer && answer.type === "noul") {
        probability = answer.probabilityYes;
        if (answer.probabilityYes >= BENEFIT_YES_MINIMUM) {
          state = "proposed";
          needed = "";
          result = "pass";
          reason = "benefit_pass";
        } else {
          needed = "benefit_below_threshold";
          result = "fail";
          reason = "benefit_below_threshold";
        }
      }
    } else {
      reason = outcome.response.failure.category;
    }
    await this.putReceipt({
      caseId: null,
      gateId: "expansion.benefit",
      policyVersion: "expansion@1",
      questionType: "noul",
      provider: request.provider ?? "unknown",
      probabilities: { yes: probability, no: 1 - probability },
      thresholds: { benefitYesMinimum: BENEFIT_YES_MINIMUM },
      selectedOption: state,
      result,
      reasonCode: reason,
      latencyMs: outcome.response.ok ? outcome.response.success.elapsedMs : null,
    });
    await this.deps.store.learning.putCandidate({
      candidateId,
      signature,
      state,
      because: state === "proposed" ? because : "",
      needed,
      updatedAt: this.deps.clock.now().toISOString(),
    });
  }

  private async maybeReview(): Promise<void> {
    const inspected = await inspectRuntime(this.deps.store.learning, [], {
      runId: "",
      commit: "",
      queueDepth: 0,
      logPath: "",
      logWritable: false,
      logError: null,
      mode: "live",
      retention: "7d",
    });
    if (!inspected.review.trigger) return;
    const reviews = await this.deps.store.learning.listReviews();
    if (reviews.some((review) => review.triggerCode === inspected.review.trigger)) return;
    await this.deps.store.learning.putReview({
      reviewId: this.deps.ids.next("review"),
      triggerCode: inspected.review.trigger,
      at: this.deps.clock.now().toISOString(),
      findings: [`review_due:${inspected.review.trigger}`],
    });
    await this.emitTrace({
      type: "review.created",
      reasonCode: inspected.review.trigger,
      selectedOutcome: "recommendation_only",
    });
  }

  private async putReceipt(input: Omit<ReceiptRecord, "receiptId" | "retries" | "createdAt">): Promise<void> {
    await this.deps.store.learning.putReceipt({
      ...input,
      receiptId: this.deps.ids.next("receipt"),
      retries: 0,
      createdAt: this.deps.clock.now().toISOString(),
    });
  }

  private async emitTrace(partial: Omit<TraceEventV1, "schemaVersion" | "sequence" | "at">): Promise<void> {
    const event: TraceEventV1 = {
      schemaVersion: 1,
      sequence: this.traces.length + 1,
      at: this.deps.clock.now().toISOString(),
      ...partial,
    };
    this.traces.push(event);
    if (!this.deps.trace) {
      this.logError = "trace_sink_missing";
      return;
    }
    try {
      await this.deps.trace.append(event);
      this.logError = null;
    } catch (error) {
      this.logError = error instanceof Error ? error.message : "trace_write_failed";
    }
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
