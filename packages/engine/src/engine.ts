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
  ActionCard,
} from "@relay/contracts";
import { isRetryableJudgmentFailure, localOnlyPolicy } from "@relay/contracts";
import type { EpisodeDefinition } from "./episodes.js";
import { inspectRuntime } from "./inspect.js";
import { runJudgmentLifecycle } from "./judgment-lifecycle.js";
import {
  BENEFIT_YES_MINIMUM,
  foldPattern,
  patternReady,
  RETENTION_LABEL,
  workSignature,
  type EpisodeOutcome,
  type ReceiptRecord,
} from "./learning-store.js";
import {
  askedToken,
  definitionSearchTask,
  evaluateChoiceGate,
  parseBirthdayUtterance,
  parseFlexibleDate,
  parseGlossaryMeans,
  shouldCreateCaseForFinal,
  validateBirthday,
  validateChoiceDistribution,
} from "./policies.js";
import { projectSnapshot } from "./projections.js";
import { PRIORITY_DIRECT, PRIORITY_OBSERVED, type WorkItem } from "./queue.js";
import { JUDGMENT_MAX_ATTEMPTS, backoffMs, knownReason, type RuntimeEventV2 } from "./runtime-events.js";
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
  readonly episodeDefinitions?: readonly EpisodeDefinition[];
  readonly glasses?: GlassesDisplayPort;
  readonly storageDetail?: string;
  readonly jevStatus?: { readonly ok: boolean; readonly detail: string };
  readonly modelStatus?: { readonly ok: boolean; readonly detail: string };
  readonly mode?: "live" | "recorded" | "replay";
  readonly gitCommit?: string;
  readonly trace?: TraceSink;
};

type WorkDisposition =
  | { readonly kind: "complete" }
  | { readonly kind: "retry"; readonly reasonCode: string; readonly delayMs: number; readonly payload: Record<string, unknown> }
  | { readonly kind: "dead"; readonly reasonCode: string };

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
  private traces: RuntimeEventV2[] = [];
  private logError: string | null = null;
  private activeEpisodeId: string | null = null;
  private activeCaseId: string | null = null;
  private currentInputPreview: string | null = null;
  private readonly optionLabels = new Map<string, Record<string, string>>();
  private readonly pendingCards = new Map<string, ActionCard>();

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
    const deadLetters = (await this.deps.store.listDeadLetters()).length;
    const inspected = await inspectRuntime(this.deps.store.learning, this.traces, {
      runId: this.runId(),
      commit: this.deps.gitCommit ?? "unknown",
      queueDepth: snapshot.queueDepth,
      logPath: this.deps.trace?.directoryLabel ?? "",
      logWritable: this.logError === null && this.deps.trace != null,
      logError: this.logError,
      mode: this.deps.mode ?? "live",
      retention: RETENTION_LABEL,
      deadLetters,
      storageAdapter: this.deps.storageDetail ?? "memory",
      activeCaseId: this.activeCaseId,
      episodeId: this.activeEpisodeId,
    });
    const gate = inspected.gate
      ? { ...inspected.gate, optionLabels: { ...inspected.gate.optionLabels, ...(this.optionLabels.get(inspected.gate.gateId) ?? {}) } }
      : null;
    return {
      ...snapshot,
      ...inspected,
      gate,
      decision: inspected.decision,
      currentInputPreview: this.currentInputPreview,
      actions: [...this.pendingCards.values()],
    };
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
        this.currentInputPreview = command.text;
        const segment = this.makeTypedSegment(command.text);
        const caseId = await this.ingestFinalSegment(segment, true);
        return { ok: true, summary: "ask_accepted", caseId };
      }
      case "UpsertGlossaryEntry":
        return this.upsertGlossary(command.token, command.expansion, command.confirmed, command.replace === true);
      case "CaptureBirthday":
        return this.captureBirthday(command.displayName, command.date, command.confirmed, command.replace === true);
      case "DeleteMemory":
        await this.deps.store.learning.deleteMemory(command.kind, command.key);
        await this.emitSnapshot();
        return { ok: true, summary: "deleted" };
      case "ApproveCandidate":
        return this.setCandidateState(command.candidateId, "approved", "user_approval");
      case "RejectCandidate":
        return this.setCandidateState(command.candidateId, "rejected", "user_reject");
      case "SnoozeCandidate":
        return this.setCandidateState(command.candidateId, "snoozed", "user_snooze");
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
    await this.note("source.rejected");
      await this.emitSnapshot();
      return "";
    }

    await this.note("source.accepted");

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
        textArtifactId: artifact.artifactId,
        textSha256: sha256,
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
        const disposition = await this.process(item);
        if (disposition.kind === "retry") {
          const available = new Date(this.deps.clock.now().getTime() + disposition.delayMs).toISOString();
          await this.deps.store.requeue(item.workId, available, disposition.payload);
        } else if (disposition.kind === "dead") {
          await this.deps.store.deadLetter(item.workId, disposition.reasonCode, this.deps.clock.now().toISOString());
        } else {
          await this.scheduler.complete(item.workId);
        }
        await this.emitSnapshot();
      } catch {
        await this.deps.store.deadLetter(item.workId, "work_failed", this.deps.clock.now().toISOString());
        await this.emitTrace({ type: "work.failed", stage: "work", status: "failed", reasonCode: "work_failed" });
      } finally {
        this.activeCaseId = null;
        this.activeEpisodeId = null;
      }
    }
  }

  private async process(item: WorkItem): Promise<WorkDisposition> {
    this.activeCaseId = typeof item.payload.caseId === "string" ? item.payload.caseId : null;
    switch (item.type) {
      case "source.final":
        return this.onSourceFinal(item);
      case "judgment.requested":
        return this.onJudgmentRequested(item);
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

  private async onSourceFinal(item: WorkItem): Promise<WorkDisposition> {
    const caseId = String(item.payload.caseId);
    const caseVersion = Number(item.payload.caseVersion);
    const sourceEventId = String(item.payload.sourceEventId);
    const text = await this.loadText(item);
    const isAsk = item.payload.isAsk === true;
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.version !== caseVersion || text == null) return { kind: "complete" };

    const birthday = parseBirthdayUtterance(text);
    if (birthday) {
      await this.stageBirthday(birthday);
      await this.finishCase(caseId, current.version, "completed");
      return { kind: "complete" };
    }

    const glossaryMeans = parseGlossaryMeans(text);
    if (glossaryMeans) {
      await this.offerGlossary(glossaryMeans.token, glossaryMeans.expansion);
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "task",
        summary: `Confirm ${glossaryMeans.token} means ${glossaryMeans.expansion}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.finishCase(caseId, current.version, "completed");
      await this.emitSnapshot();
      return { kind: "complete" };
    }

    const updated = await this.deps.store.updateCase(caseId, caseVersion, {
      phase: "detect",
      status: "active",
      at: this.deps.clock.now().toISOString(),
    });
    if (!updated) return { kind: "complete" };

    await this.deps.store.appendCaseEvent(caseId, updated.version, "case.phase_changed", updated.updatedAt, {
      phase: "detect",
      sourceEventId,
    });

    const reflex = await this.runReflexesOnFinal(text, isAsk, caseId, updated.version, sourceEventId);
    if (reflex.clarify) {
      await this.deps.store.updateCase(caseId, updated.version, {
        phase: "judge",
        status: "waiting",
        waitKind: "judgment",
        at: this.deps.clock.now().toISOString(),
      });
      await this.scheduler.enqueue(
        "judgment.requested",
        {
          caseId,
          token: reflex.clarify.token,
          reflexId: reflex.clarify.reflexId,
          prompt: reflex.clarify.prompt,
          attempt: 1,
          explicitAsk: isAsk,
          sourceEventId,
        },
        PRIORITY_DIRECT,
        this.deps.ids,
      );
      await this.emitTrace({
        type: "judgment.requested",
        stage: "judgment.request",
        status: "waiting",
        caseId,
        reasonCode: "choice",
        attempt: 1,
      });
      return { kind: "complete" };
    }

    const findingSummaries = [...reflex.findings];
    const token = askedToken(text);
    let signature = reflex.signature;
    if (findingSummaries.length === 0 && token) {
      const memory = await this.deps.store.learning.getMemory("glossary", token);
      const expansion = memory?.source === "explicit_user" ? (memory.value.expansion ?? "") : "";
      if (expansion) {
        findingSummaries.push(`${token}: ${expansion}`);
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
    if (!latest) return { kind: "complete" };
    const completed = await this.deps.store.updateCase(caseId, latest.version, {
      phase: "done",
      status: "completed",
      at: this.deps.clock.now().toISOString(),
    });
    if (!completed) return { kind: "complete" };

    if (current.origin === "direct") {
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "ask",
        summary: structuredAskSummary(text),
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
      try {
        const unresolved = signature.includes("outcome=no_candidates");
        await this.recordEpisode(signature, caseId, unresolved ? "unresolved" : "completed");
      } catch {
        // Episode persistence must not block the outcome receipt for the Ask.
      }
    }
    await this.emitTrace({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId,
      reasonCode: signature ? "episode_recorded" : "no_episode",
    });
    return { kind: "complete" };
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
    clarify: { token: string; prompt: string; reflexId: string } | null;
    suppressSearch: boolean;
  }> {
    const findings: string[] = [];
    let signature: string | null = null;
    const suppressSearch = false;
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
        stage: "reflex.detect",
        status: "completed",
        caseId,
        reflexId: reflex.definition.id,
        reasonCode: "detected",
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
            stage: "policy.evaluate",
            status: "completed",
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: "exact_glossary",
          });
          await this.offerGlossary(trigger.token, result.summary.split(": ").slice(1).join(": "));
        } else if (result.type === "clarification_required") {
          return {
            findings,
            signature,
            clarify: {
              token: trigger.token,
              prompt: result.clarificationPrompt ?? "",
              reflexId: reflex.definition.id,
            },
            suppressSearch: true,
          };
        } else if (result.summary === "no_candidates") {
          signature = workSignature("acronym.lookup", { outcome: "no_candidates", token: trigger.token });
          await this.emitTrace({
            type: "policy.evaluated",
            stage: "policy.evaluate",
            status: "completed",
            caseId,
            reflexId: reflex.definition.id,
            reasonCode: "no_candidates",
          });
        }
      }
    }
    return { findings, signature, clarify: null, suppressSearch };
  }

  private async onJudgmentRequested(item: WorkItem): Promise<WorkDisposition> {
    const caseId = String(item.payload.caseId ?? "");
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.status === "completed" || current.status === "blocked" || current.status === "failed") {
      return { kind: "complete" };
    }
    const attempt = Number(item.payload.attempt ?? 1);
    const reflexId = String(item.payload.reflexId ?? "resolve-acronym");
    await this.deps.store.upsertJudgmentAttempt({
      attemptId: `${caseId}:${attempt}`,
      caseId,
      workId: item.workId,
      attempt,
      maxAttempts: JUDGMENT_MAX_ATTEMPTS,
      nextAttemptAt: null,
      failureCategory: null,
      providerRequestId: null,
      createdAt: this.deps.clock.now().toISOString(),
    });
    let parsed: { optionIds?: string[]; token?: string; choiceProbabilityMinimum?: number; choiceMarginMinimum?: number } = {};
    try {
      parsed = JSON.parse(String(item.payload.prompt ?? "")) as typeof parsed;
    } catch {
      await this.finishCase(caseId, current.version, "failed");
      return { kind: "dead", reasonCode: "invalid_gate" };
    }
    const labels = parsed.optionIds ?? [];
    const optionIds = labels.map((_, index) => `opt_${index + 1}`);
    const labelById: Record<string, string> = { no_match: "no_match" };
    const idByLabel = new Map<string, string>();
    labels.forEach((label, index) => {
      const id = optionIds[index] ?? `opt_${index + 1}`;
      labelById[id] = label;
      idByLabel.set(label, id);
    });
    this.optionLabels.set(reflexId, labelById);
    const minimum = parsed.choiceProbabilityMinimum ?? 0.65;
    const marginMinimum = parsed.choiceMarginMinimum ?? 0.15;
    const criteria: Record<string, string> = {};
    for (const id of optionIds) criteria[id] = labelById[id] ?? id;
    criteria.no_match = "None of the permitted choices";
    labelById.no_match = "None of the permitted choices";
    const request: JudgmentRequest = {
      questionSetId: "judgment.acronym-choice",
      questionSetVersion: "1",
      model: "jev-1.13.0",
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: {
        token: String(item.payload.token ?? parsed.token ?? ""),
        optionCount: optionIds.length,
        explicitAsk: item.payload.explicitAsk === true,
        provenance: "glossary_window",
        policyVersion: "resolve-acronym@1",
        sourceRef: String(item.payload.sourceEventId ?? ""),
      },
      questions: {
        expansion: { type: "choice", instructions: "Select one allowed option.", criteria, requireNoMatch: true },
      },
      caseId,
      caseVersion: current.version,
    };
    const started = Date.now();
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
    const durationMs = Date.now() - started;
    const requestedAt = new Date(started).toISOString();
    const completedAt = this.deps.clock.now().toISOString();
    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      const reasonCode = knownReason(category);
      const retrying = isRetryableJudgmentFailure(category) && attempt < JUDGMENT_MAX_ATTEMPTS;
      const blocked = category === "missing_secret" || category === "authentication";
      await this.putReceipt({
        caseId,
        gateId: reflexId,
        policyVersion: "resolve-acronym@1",
        questionType: "choice",
        provider: request.provider ?? "unknown",
        probabilities: {},
        thresholds: { choiceProbabilityMinimum: minimum, choiceMarginMinimum: marginMinimum },
        selectedOption: null,
        selectedOptionId: null,
        optionLabels: labelById,
        reflexId,
        judgmentId: outcome.record.judgmentId,
        result: retrying ? "wait" : blocked ? "blocked" : "fail",
        reasonCode,
        latencyMs: durationMs,
        retries: attempt - 1,
        requestedAt,
        completedAt: retrying ? null : completedAt,
      });
      if (retrying) {
        const nextAttemptAt = new Date(this.deps.clock.now().getTime() + backoffMs(attempt)).toISOString();
        await this.deps.store.upsertJudgmentAttempt({
          attemptId: `${caseId}:${attempt}`,
          caseId,
          workId: item.workId,
          attempt,
          maxAttempts: JUDGMENT_MAX_ATTEMPTS,
          nextAttemptAt,
          failureCategory: category,
          providerRequestId: outcome.record.judgmentId,
          createdAt: this.deps.clock.now().toISOString(),
        });
        await this.emitTrace({
          type: "judgment.failed",
          stage: "judgment.response",
          status: "waiting",
          caseId,
          reflexId,
          judgmentId: outcome.record.judgmentId,
          reasonCode,
          attempt,
          durationMs,
        });
        return {
          kind: "retry",
          reasonCode,
          delayMs: backoffMs(attempt),
          payload: { ...item.payload, attempt: attempt + 1, caseVersion: current.version },
        };
      }
      const status = blocked ? "blocked" : "failed";
      await this.finishCase(caseId, current.version, status);
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "wait",
        summary: `Jev choice unavailable · ${reasonCode}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.emitTrace({
        type: "judgment.failed",
        stage: "judgment.response",
        status: "failed",
        caseId,
        reflexId,
        judgmentId: outcome.record.judgmentId,
        reasonCode,
        attempt,
        durationMs,
      });
      return { kind: "dead", reasonCode };
    }
    const answer = outcome.response.success.answers.expansion;
    if (!answer || answer.type !== "choice") {
      await this.finishCase(caseId, current.version, "failed");
      return { kind: "dead", reasonCode: "invalid_response" };
    }
    const probabilities: Record<string, number> = {};
    for (const [key, value] of Object.entries(answer.probabilities)) {
      const id = idByLabel.get(key) ?? (key === "no_match" || optionIds.includes(key) ? key : "");
      if (!id) {
        await this.finishCase(caseId, current.version, "failed");
        return { kind: "dead", reasonCode: "not_in_options" };
      }
      probabilities[id] = value;
    }
    const declared = idByLabel.get(answer.choice) ?? answer.choice;
    const distribution = validateChoiceDistribution({ probabilities, declared, allowed: optionIds });
    if (!distribution.ok) {
      await this.finishCase(caseId, current.version, "failed");
      return { kind: "dead", reasonCode: knownReason(distribution.reasonCode) };
    }
    const gate = evaluateChoiceGate({ probabilities, minimum, marginMinimum });
    const pass = gate.pass && gate.selected === declared;
    const reasonCode = pass ? "policy_pass" : knownReason(gate.reasonCode);
    await this.putReceipt({
      caseId,
      gateId: reflexId,
      policyVersion: "resolve-acronym@1",
      questionType: "choice",
      provider: request.provider ?? "unknown",
      probabilities,
      thresholds: { choiceProbabilityMinimum: minimum, choiceMarginMinimum: marginMinimum },
      selectedOption: gate.selected,
      selectedOptionId: gate.selected,
      optionLabels: labelById,
      reflexId,
      judgmentId: outcome.record.judgmentId,
      result: pass ? "pass" : "fail",
      reasonCode,
      latencyMs: durationMs,
      retries: attempt - 1,
      requestedAt,
      completedAt,
    });
    await this.emitTrace({
      type: "judgment.completed",
      stage: "judgment.response",
      status: "completed",
      caseId,
      reflexId,
      judgmentId: outcome.record.judgmentId,
      reasonCode,
      durationMs,
      attempt,
    });
    const label = labelById[gate.selected] ?? "";
    if (pass && gate.selected !== "no_match" && label) {
      await this.finishCase(caseId, current.version, "completed");
      await this.deps.store.addFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "answer",
        summary: label,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      const token = String(item.payload.token ?? "");
      const signature = workSignature("acronym.lookup", { outcome: "choice", token });
      if (signature) await this.recordEpisode(signature, caseId, "completed");
      await this.offerGlossary(token, label);
    } else {
      await this.finishCase(caseId, current.version, "completed");
    }
    return { kind: "complete" };
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

  private async upsertGlossary(
    rawToken: string,
    expansion: string,
    confirmed: boolean,
    replace: boolean,
  ): Promise<RelayCommandResult> {
    const token = rawToken.trim().toUpperCase();
    const text = expansion.trim();
    if (!/^[A-Z0-9]{2,12}$/.test(token)) return { ok: false, summary: "invalid_token" };
    if (!text) return { ok: false, summary: "empty_expansion" };
    if (!confirmed) return { ok: false, summary: "confirmation_required" };
    const existing = await this.deps.store.learning.getMemory("glossary", token);
    if (existing && existing.source === "explicit_user" && existing.value.expansion !== text && !replace) {
      return { ok: false, summary: "conflict" };
    }
    await this.deps.store.learning.putMemory({
      memoryId: existing?.memoryId ?? this.deps.ids.next("mem"),
      kind: "glossary",
      key: token,
      value: { expansion: text, status: "confirmed" },
      source: "explicit_user",
      createdAt: existing?.createdAt ?? this.deps.clock.now().toISOString(),
    });
    for (const [id, card] of this.pendingCards) {
      if (card.kind === "save_definition" && card.token === token) this.pendingCards.delete(id);
    }
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
    await this.emitTrace({
      type: "memory.stored",
      stage: "memory.write",
      status: "completed",
      reasonCode: "explicit_user",
    });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: `Saved ${token}`,
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitSnapshot();
    return { ok: true, summary: "remembered" };
  }

  private async captureBirthday(
    displayName: string,
    date: string,
    confirmed: boolean,
    replace: boolean,
  ): Promise<RelayCommandResult> {
    const invalid = validateBirthday(displayName, date);
    if (invalid) return { ok: false, summary: invalid };
    const parsed = parseFlexibleDate(date);
    if (!parsed) return { ok: false, summary: "invalid_date" };
    if (!confirmed) return { ok: false, summary: "confirmation_required" };
    const key = await this.personId(displayName);
    const existing = await this.deps.store.learning.getMemory("birthday", key);
    const next = `${parsed.year ?? ""}-${parsed.month}-${parsed.day}`;
    const previous = existing ? `${existing.value.year ?? ""}-${existing.value.month}-${existing.value.day}` : "";
    if (existing && existing.source === "explicit_user" && previous !== next && !replace) {
      return { ok: false, summary: "conflict" };
    }
    await this.deps.store.learning.putMemory({
      memoryId: existing?.memoryId ?? this.deps.ids.next("mem"),
      kind: "birthday",
      key,
      value: {
        displayName: displayName.normalize("NFKC").trim(),
        month: String(parsed.month),
        day: String(parsed.day),
        year: parsed.year ? String(parsed.year) : "",
      },
      source: "explicit_user",
      createdAt: existing?.createdAt ?? this.deps.clock.now().toISOString(),
    });
    for (const [id, card] of this.pendingCards) {
      if (card.kind === "confirm_birthday" && card.personId === key) this.pendingCards.delete(id);
    }
    await this.emitTrace({
      type: "memory.stored",
      stage: "memory.write",
      status: "completed",
      reasonCode: "birthday_confirmed",
    });
    await this.putReceipt({
      caseId: null,
      gateId: "memory.birthday",
      policyVersion: "memory@1",
      questionType: "user",
      provider: "user",
      probabilities: {},
      thresholds: {},
      selectedOption: key,
      result: "pass",
      reasonCode: "birthday_confirmed",
      latencyMs: null,
    });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: "Birthday saved",
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.emitSnapshot();
    return { ok: true, summary: "birthday_stored" };
  }

  async completeVerifiedWork(kind: string, fields: Readonly<Record<string, string>>): Promise<RelayCommandResult> {
    const definition = this.deps.episodeDefinitions?.find((item) => item.kind === kind);
    if (!definition?.candidateTemplateId && kind !== definition?.kind) return { ok: false, summary: "no_definition" };
    if (!definition) return { ok: false, summary: "no_definition" };
    const signature = definition.normalize(fields);
    if (!signature) return { ok: false, summary: "invalid_signature" };
    await this.putReceipt({
      caseId: null,
      gateId: definition.candidateTemplateId ?? definition.kind,
      policyVersion: "episode@1",
      questionType: "not_applicable",
      provider: "not_applicable",
      probabilities: {},
      thresholds: {},
      selectedOption: null,
      result: "not_applicable",
      reasonCode: "episode_recorded",
      latencyMs: null,
    });
    await this.recordEpisode(signature, null, "completed");
    await this.emitSnapshot();
    return { ok: true, summary: "episode_recorded" };
  }

  private async setCandidateState(
    candidateId: string,
    state: "approved" | "rejected" | "snoozed",
    reasonCode: "user_approval" | "user_reject" | "user_snooze",
  ): Promise<RelayCommandResult> {
    const candidates = await this.deps.store.learning.listCandidates();
    const current = candidates.find((candidate) => candidate.candidateId === candidateId);
    if (!current || current.state !== "proposed") return { ok: false, summary: "not_proposed" };
    await this.deps.store.learning.putCandidate({
      ...current,
      state,
      needed: "",
      updatedAt: this.deps.clock.now().toISOString(),
    });
    await this.emitTrace({
      type: state === "approved" ? "candidate.approved" : "candidate.rejected",
      stage: "proposal.create",
      status: "completed",
      reasonCode,
    });
    await this.emitSnapshot();
    return { ok: true, summary: state };
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
    const successful = episodes.filter((episode) => episode.outcome === "completed");
    const termination = successful.length === 0 ? "abandoned" : "completed";
    await this.deps.store.learning.closeSession(
      open.sessionId,
      this.deps.clock.now().toISOString(),
      termination,
      successful.length,
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
    outcome: EpisodeOutcome,
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
    if (outcome === "completed") {
      const existing = await this.deps.store.learning.getPattern(signature);
      const pattern = foldPattern(existing, {
        episodeId,
        sessionId: open.sessionId,
        caseId,
        signature,
        outcome,
        startedAt: at,
        completedAt: at,
      });
      await this.deps.store.learning.putPattern(pattern);
      if (patternReady(pattern)) await this.considerCandidate(pattern.signature, pattern.count, pattern.sessionIds.length);
    }
    this.activeEpisodeId = outcome === "completed" ? null : episodeId;
    await this.emitTrace({
      type: "episode.recorded",
      stage: "episode.complete",
      status: "completed",
      ...(caseId ? { caseId } : {}),
      episodeId,
      reasonCode: outcome === "completed" ? "completed" : "unresolved",
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
    const kind = signature.split("|")[0] ?? "";
    const definition = this.deps.episodeDefinitions?.find((item) => item.kind === kind);
    if (!definition?.candidateTemplateId) return;
    const because = definition.render?.({ count, sessions }) ?? "";
    if (!because) return;
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
      probabilities: outcome.response.ok ? { yes: probability } : {},
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
    const deadLetters = (await this.deps.store.listDeadLetters()).length;
    const inspected = await inspectRuntime(this.deps.store.learning, [], {
      runId: this.runId(),
      commit: this.deps.gitCommit ?? "unknown",
      queueDepth: 0,
      logPath: "",
      logWritable: false,
      logError: null,
      mode: this.deps.mode ?? "live",
      retention: RETENTION_LABEL,
      deadLetters,
      storageAdapter: this.deps.storageDetail ?? "memory",
      activeCaseId: null,
      episodeId: null,
    });
    if (!inspected.review.trigger) return;
    await this.deps.store.learning.putReview({
      reviewId: this.deps.ids.next("review"),
      triggerCode: inspected.review.trigger,
      at: this.deps.clock.now().toISOString(),
      findings: ["recommendation_only"],
      sessionsAtReview: inspected.review.completeSessions,
      episodesAtReview: inspected.review.completeEpisodes,
      candidatesAtReview: inspected.review.qualifiedCandidates,
      builtReflexesAtReview: inspected.review.builtReflexes,
    });
    await this.emitTrace({
      type: "review.created",
      stage: "review.evaluate",
      status: "completed",
      reasonCode: knownReason(inspected.review.trigger),
    });
  }

  private async putReceipt(
    input: Omit<
      ReceiptRecord,
      | "receiptId"
      | "retries"
      | "createdAt"
      | "decisionId"
      | "optionLabels"
      | "selectedOptionId"
      | "judgmentId"
      | "reflexId"
      | "requestedAt"
      | "completedAt"
    > & {
      retries?: number;
      decisionId?: string;
      optionLabels?: Readonly<Record<string, string>>;
      selectedOptionId?: string | null;
      judgmentId?: string | null;
      reflexId?: string | null;
      requestedAt?: string | null;
      completedAt?: string | null;
    },
  ): Promise<void> {
    const now = this.deps.clock.now().toISOString();
    const receiptId = this.deps.ids.next("receipt");
    await this.deps.store.learning.putReceipt({
      caseId: input.caseId,
      gateId: input.gateId,
      policyVersion: input.policyVersion,
      questionType: input.questionType,
      provider: input.provider,
      probabilities: input.probabilities,
      thresholds: input.thresholds,
      selectedOption: input.selectedOption,
      result: input.result,
      reasonCode: input.reasonCode,
      latencyMs: input.latencyMs,
      receiptId,
      decisionId: input.decisionId ?? receiptId,
      judgmentId: input.judgmentId ?? null,
      reflexId: input.reflexId ?? null,
      optionLabels: input.optionLabels ?? {},
      selectedOptionId: input.selectedOptionId ?? input.selectedOption,
      requestedAt: input.requestedAt ?? now,
      completedAt: input.completedAt ?? (input.result === "wait" ? null : now),
      retries: input.retries ?? 0,
      createdAt: now,
    });
  }

  private async emitTrace(partial: {
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
    latencyMs?: number;
    probabilities?: unknown;
    thresholds?: unknown;
    waitState?: string;
    reflexVersion?: number;
  }): Promise<void> {
    const stage = partial.stage ?? STAGE_FOR[partial.type] ?? "work";
    const event: RuntimeEventV2 = {
      schemaVersion: 2,
      sequence: this.traces.length + 1,
      runId: this.runId(),
      at: this.deps.clock.now().toISOString(),
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
      queueDepth: await this.deps.store.countWorkItems(),
    };
    if (event.eventType === "run.started" || event.eventType === "session.started" || event.eventType === "source.accepted") {
      // keep
    }
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

  private async note(eventType: string): Promise<void> {
    await this.emitTrace({
      type: eventType === "source.rejected" ? "source.rejected" : "source.accepted",
      stage: "source.accept",
      status: eventType === "source.rejected" ? "failed" : "completed",
      reasonCode: eventType === "source.rejected" ? "listening_off" : "start",
    });
  }

  private runId(): string {
    const runId = this.deps.trace?.runId ?? "run_pending";
    return /^run_[a-z0-9-]{1,40}$/.test(runId) ? runId : "run_pending";
  }

  private async loadText(item: WorkItem): Promise<string | null> {
    const artifactId = String(item.payload.textArtifactId ?? "");
    const sha256 = String(item.payload.textSha256 ?? "");
    if (!artifactId || !sha256) return null;
    const raw = await this.deps.artifacts.get({ artifactId, sha256, policy: localOnlyPolicy() });
    return new TextDecoder().decode(raw);
  }

  private async finishCase(
    caseId: string,
    version: number,
    status: "completed" | "blocked" | "failed",
  ): Promise<void> {
    const current = await this.deps.store.getCase(caseId);
    if (!current) return;
    await this.deps.store.updateCase(caseId, current.version, {
      status,
      phase: status === "completed" ? "done" : "judge",
      waitKind: null,
      at: this.deps.clock.now().toISOString(),
    });
    if (this.activeCaseId === caseId) this.currentInputPreview = null;
    void version;
  }

  private async stageBirthday(input: {
    displayName: string;
    month: number;
    day: number;
    year?: number;
  }): Promise<void> {
    const key = await this.personId(input.displayName);
    const month = String(input.month).padStart(2, "0");
    const day = String(input.day).padStart(2, "0");
    const date = input.year ? `${input.year}-${month}-${day}` : `${month}-${day}`;
    const actionId = this.deps.ids.next("action");
    this.pendingCards.set(actionId, {
      actionId,
      kind: "confirm_birthday",
      label: "Confirm birthday",
      personId: key,
      displayName: input.displayName,
      date,
    });
    await this.deps.store.addFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "task",
      summary: "Confirm birthday",
      createdAt: this.deps.clock.now().toISOString(),
    });
  }

  private async offerGlossary(token: string, expansion: string): Promise<void> {
    if (!token || !expansion) return;
    const existing = await this.deps.store.learning.getMemory("glossary", token);
    if (existing?.source === "explicit_user") return;
    const actionId = this.deps.ids.next("action");
    this.pendingCards.set(actionId, {
      actionId,
      kind: "save_definition",
      label: `Save definition for ${token}`,
      token,
      expansion,
    });
  }

  private async personId(displayName: string): Promise<string> {
    const normal = displayName.normalize("NFKC").trim().toLocaleLowerCase();
    const digest = await sha256Hex(encodeText(normal));
    return `person_${digest.slice(0, 16)}`;
  }

  private emit(change: RelayChange): void {
    for (const listener of this.listeners) listener(change);
  }

  private async emitSnapshot(): Promise<void> {
    const snapshot = await this.getSnapshot();
    this.emit({ type: "SnapshotReplaced", snapshot });
  }
}

const STAGE_FOR: Record<string, RuntimeEventV2["stage"]> = {
  "run.started": "run",
  "session.started": "session",
  "session.ended": "session",
  "source.accepted": "source.accept",
  "source.rejected": "source.accept",
  "reflex.detected": "reflex.detect",
  "policy.evaluated": "policy.evaluate",
  "judgment.requested": "judgment.request",
  "judgment.completed": "judgment.response",
  "judgment.failed": "judgment.response",
  "memory.stored": "memory.write",
  "episode.recorded": "episode.complete",
  "outcome.recorded": "episode.complete",
  "candidate.approved": "proposal.create",
  "candidate.rejected": "proposal.create",
  "review.created": "review.evaluate",
  "work.failed": "work",
};

function structuredAskSummary(text: string): string {
  if (parseGlossaryMeans(text)) return "typed ask · glossary definition";
  if (parseBirthdayUtterance(text)) return "typed ask · birthday capture";
  if (askedToken(text)) return "typed ask · acronym lookup";
  return "typed ask · general";
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
