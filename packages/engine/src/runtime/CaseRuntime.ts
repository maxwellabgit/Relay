import type { ReflexModule, RelayCommandResult, TextModelPort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import {
  askedToken,
  definitionSearchTask,
  parseBirthdayUtterance,
  parseFlexibleDate,
  parseGlossaryMeans,
  validateBirthday,
} from "../policies.js";
import { workSignature } from "../learning-store.js";
import { PRIORITY_DIRECT, type WorkItem } from "../queue.js";
import { draftAskProse, explicitSummarySource } from "../model/summarize.js";
import type { Clock, IdFactory, Scheduler } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort } from "@relay/contracts";
import {
  encodeText,
  feedItemId,
  modelWorkId,
  sha256Hex,
  type EngineTrace,
} from "../engine-helpers.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";
import type { OverlayState } from "../projections/OverlayState.js";
import type { PatternService } from "../learning/PatternService.js";
import type { WorkDisposition } from "../judgments/JudgmentService.js";
import type { AmbientTriage } from "../ambient/AmbientTriage.js";
import { putJsonArtifact } from "../protected-content.js";
import { runInTransaction } from "../transactions.js";

export type CaseRuntimeDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly model: TextModelPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly sessionId: string;
  readonly reflexModules?: readonly ReflexModule[];
  readonly scheduler: Scheduler;
  readonly outcomes: OutcomeRecorder;
  readonly overlays: OverlayState;
  readonly patterns: PatternService;
  readonly ambient: AmbientTriage;
  readonly trace: EngineTrace;
  readonly getAbortSignal: () => AbortSignal;
  readonly getActiveCaseId: () => string | null;
  readonly emitSnapshot: () => Promise<void>;
};

export class CaseRuntime {
  constructor(private readonly deps: CaseRuntimeDeps) {}

  async onSourceFinal(item: WorkItem): Promise<WorkDisposition> {
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
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    const glossaryMeans = parseGlossaryMeans(text);
    if (glossaryMeans) {
      await this.offerGlossary(glossaryMeans.token, glossaryMeans.expansion);
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "task"),
        kind: "task",
        summary: `Confirm ${glossaryMeans.token} means ${glossaryMeans.expansion}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
      await this.deps.emitSnapshot();
      return { kind: "complete" };
    }

    const updated = await runInTransaction(this.deps.store, async () => {
      const next = await this.deps.store.updateCase(caseId, caseVersion, {
        phase: "detect",
        status: "active",
        at: this.deps.clock.now().toISOString(),
      });
      if (!next) return null;
      await this.deps.store.appendCaseEvent(caseId, next.version, "case.phase_changed", next.updatedAt, {
        phase: "detect",
        sourceEventId,
      });
      return next;
    });
    if (!updated) return { kind: "complete" };

    const reflex = await this.runReflexesOnFinal(text, isAsk, caseId, updated.version, sourceEventId);
    if (reflex.clarify) {
      await this.deps.store.updateCase(caseId, updated.version, {
        phase: "judge",
        status: "waiting",
        waitKind: "judgment",
        at: this.deps.clock.now().toISOString(),
      });
      await this.deps.scheduler.enqueue(
        "judgment.requested",
        {
          caseId,
          token: reflex.clarify.token,
          reflexId: reflex.clarify.reflexId,
          prompt: await sealClarificationPrompt(this.deps.artifacts, reflex.clarify.prompt),
          attempt: 1,
          explicitAsk: isAsk,
          sourceEventId,
          contextExcerpt: text.slice(0, 400),
          textArtifactId: String(item.payload.textArtifactId ?? ""),
          textSha256: String(item.payload.textSha256 ?? ""),
        },
        PRIORITY_DIRECT,
        this.deps.ids,
        0,
        { parentWorkId: item.workId, correlationId: caseId },
      );
      await this.deps.trace.emit({
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
    const latest = await this.deps.store.getCase(caseId);
    if (!latest) return { kind: "complete" };
    const at = this.deps.clock.now().toISOString();

    if (current.origin === "direct") {
      const classification = classifyAskText(text);
      await this.deps.store.appendCaseEvent(caseId, current.version, "ask.classified", at, {
        classification,
      });
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "ask"),
        kind: "ask",
        summary: text,
        createdAt: at,
        caseId,
      });
    }

    if (findingSummaries.length > 0) {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "answer"),
        kind: "answer",
        summary: findingSummaries.join(" · "),
        createdAt: at,
        caseId,
      });
      await this.deps.trace.emit({
        type: "answer.committed",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: "completed",
      });
      if (signature) {
        try {
          const unresolved = signature.includes("outcome=no_candidates");
          await this.deps.patterns.recordEpisode(signature, caseId, unresolved ? "unresolved" : "completed");
        } catch {
          // Episode persistence must not block the Ask outcome.
        }
      }
      await this.deps.trace.emit({
        type: "outcome.recorded",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: signature ? "episode_recorded" : "no_episode",
      });
      return this.finishObservedCase(caseId, latest.version, item, text, current.origin);
    }

    if (current.origin === "direct") {
      const task = definitionSearchTask(text);
      if (task && token && !reflex.suppressSearch) {
        signature = workSignature("acronym.lookup", { outcome: "no_candidates", token });
        await this.deps.outcomes.putReceipt({
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
        await this.deps.outcomes.publishFeedItem({
          itemId: feedItemId(caseId, "task"),
          kind: "task",
          summary: task,
          createdAt: at,
          caseId,
        });
        if (signature) {
          try {
            await this.deps.patterns.recordEpisode(signature, caseId, "unresolved");
          } catch {
            // ignore
          }
        }
        await this.deps.trace.emit({
          type: "outcome.recorded",
          stage: "episode.complete",
          status: "completed",
          caseId,
          reasonCode: "episode_recorded",
        });
        await this.deps.outcomes.finishCase(caseId, latest.version, "completed", this.deps.getActiveCaseId());
        return { kind: "complete" };
      }
      if (task) {
        await this.deps.outcomes.publishFeedItem({
          itemId: feedItemId(caseId, "task"),
          kind: "task",
          summary: task,
          createdAt: at,
          caseId,
        });
        await this.deps.trace.emit({
          type: "outcome.recorded",
          stage: "episode.complete",
          status: "completed",
          caseId,
          reasonCode: "no_episode",
        });
        await this.deps.outcomes.finishCase(caseId, latest.version, "completed", this.deps.getActiveCaseId());
        return { kind: "complete" };
      }

      const waiting = await this.deps.store.updateCase(caseId, latest.version, {
        phase: "decide",
        status: "waiting",
        waitKind: "tool",
        at,
      });
      if (!waiting) return { kind: "complete" };
      await this.deps.store.enqueue({
        workId: modelWorkId(caseId),
        type: "tool.route",
        priority: PRIORITY_DIRECT,
        availableAt: at,
        createdAt: at,
        payload: {
          caseId,
          caseVersion: waiting.version,
          sourceEventId,
          text,
          textArtifactId: String(item.payload.textArtifactId ?? ""),
          textSha256: String(item.payload.textSha256 ?? ""),
          toolSteps: 0,
          judgmentRounds: 0,
          sourceAttempts: 0,
        },
        parentWorkId: item.workId,
        correlationId: caseId,
      });
      this.deps.scheduler.kick();
      await this.deps.trace.emit({
        type: "judgment.requested",
        stage: "judgment.request",
        status: "waiting",
        caseId,
        reasonCode: "choice",
      });
      return { kind: "complete" };
    }

    await this.deps.trace.emit({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId,
      reasonCode: "no_episode",
    });
    return this.finishObservedCase(caseId, latest.version, item, text, current.origin);
  }

  async onModelRequested(item: WorkItem): Promise<WorkDisposition> {
    const caseId = String(item.payload.caseId ?? "");
    const current = await this.deps.store.getCase(caseId);
    if (
      !current ||
      current.status === "completed" ||
      current.status === "blocked" ||
      current.status === "failed" ||
      current.status === "cancelled"
    ) {
      return { kind: "complete" };
    }
    const text = await this.loadText(item);
    if (text == null) {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "answer"),
        kind: "answer",
        summary: "No local result for this Ask.",
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.trace.emit({
        type: "answer.committed",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: "model_unavailable",
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    const existing = (await this.deps.store.listFeedItemRecords()).find(
      (feed) => feed.itemId === feedItemId(caseId, "answer"),
    );
    if (!existing) {
      const generated = await this.generateDirectAnswer(text, caseId);
      if (this.deps.getAbortSignal().aborted || (!generated.ok && generated.failureReason === "cancelled")) {
        return { kind: "complete" };
      }
      const latestCase = await this.deps.store.getCase(caseId);
      if (!latestCase || latestCase.status === "cancelled") return { kind: "complete" };
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "answer"),
        kind: "answer",
        summary: generated.ok ? generated.text : "No local result for this Ask.",
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.trace.emit({
        type: "answer.committed",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: generated.ok ? "completed" : "model_unavailable",
      });
    }
    await this.deps.trace.emit({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId,
      reasonCode: "no_episode",
    });
    await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
    return { kind: "complete" };
  }

  async upsertGlossary(
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
    await this.deps.overlays.clearActions(
      (card) => card.kind === "save_definition" && card.token === token,
    );
    await this.deps.outcomes.putReceipt({
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
    await this.deps.trace.emit({
      type: "memory.stored",
      stage: "memory.write",
      status: "completed",
      reasonCode: "explicit_user",
    });
    await this.deps.outcomes.publishFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: `Saved ${token}`,
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: "remembered" };
  }

  async captureBirthday(
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
    await this.deps.overlays.clearActions(
      (card) => card.kind === "confirm_birthday" && card.personId === key,
    );
    await this.deps.trace.emit({
      type: "memory.stored",
      stage: "memory.write",
      status: "completed",
      reasonCode: "birthday_confirmed",
    });
    await this.deps.outcomes.putReceipt({
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
    await this.deps.outcomes.publishFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "memory",
      summary: "Birthday saved",
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: "birthday_stored" };
  }

  async stageBirthday(input: {
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
    await this.deps.overlays.stageAction({
      actionId,
      kind: "confirm_birthday",
      label: "Confirm birthday",
      personId: key,
      displayName: input.displayName,
      date,
    });
    await this.deps.outcomes.publishFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: "task",
      summary: "Confirm birthday",
      createdAt: this.deps.clock.now().toISOString(),
    });
  }

  async offerGlossary(token: string, expansion: string): Promise<void> {
    if (!token || !expansion) return;
    const existing = await this.deps.store.learning.getMemory("glossary", token);
    if (existing?.source === "explicit_user") return;
    const actionId = this.deps.ids.next("action");
    await this.deps.overlays.stageAction({
      actionId,
      kind: "save_definition",
      label: `Save definition for ${token}`,
      token,
      expansion,
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

    const modules = [...(this.deps.reflexModules ?? [])].sort((left, right) => {
      const rank = (mode: string) => (mode === "explicit_utterance" ? 0 : 1);
      return rank(left.definition.approvalMode) - rank(right.definition.approvalMode);
    });
    let explicitHandled = false;
    for (const reflex of modules) {
      if (explicitHandled && reflex.definition.id === "reflex.resolve-acronym") continue;
      const triggers = reflex.detect(sourceEvent, detection);
      await this.deps.trace.emit({
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

        if (result.type === "finding" && reflex.definition.id !== "reflex.resolve-acronym") {
          if (reflex.definition.approvalMode !== "explicit_utterance") continue;
          if (this.deps.getAbortSignal().aborted) continue;
          await this.persistReviewedFinding(reflex.definition.id, trigger.token, caseId);
          explicitHandled = true;
          findings.push(result.summary);
          signature = workSignature(reflex.definition.id, { token: trigger.token });
          continue;
        }
        if (result.type === "finding") {
          findings.push(result.summary);
          signature = workSignature("acronym.lookup", { outcome: "exact", token: trigger.token });
          await this.deps.outcomes.putReceipt({
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
          await this.deps.trace.emit({
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
          await this.deps.trace.emit({
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

  private async persistReviewedFinding(reflexId: string, token: string, caseId: string): Promise<void> {
    if (this.deps.getAbortSignal().aborted) return;
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.status === "cancelled") return;
    const record =
      reflexId === "reflex.capture-note"
        ? { kind: "note" as const, recordType: "note" }
        : reflexId === "reflex.remember-fact"
          ? { kind: "fact" as const, recordType: "fact" }
          : { kind: "recommendation" as const, recordType: "recommendation" };
    const text = token.trim();
    await this.deps.store.learning.putMemory({
      memoryId: this.deps.ids.next("memory"),
      kind: record.kind,
      key: `${record.recordType}:${text.slice(0, 120) || "open"}`,
      value: { text, recordType: record.recordType, status: "accepted", reflexId, caseId },
      source: "explicit_user",
      createdAt: this.deps.clock.now().toISOString(),
    });
    await this.deps.trace.emit({
      type: "policy.evaluated",
      stage: "policy.evaluate",
      status: "completed",
      caseId,
      reflexId,
      reasonCode: record.recordType,
    });
  }

  private async generateDirectAnswer(
    text: string,
    caseId: string,
  ): Promise<{ ok: true; text: string } | { ok: false; failureReason: string }> {
    const signal = this.deps.getAbortSignal();
    await this.deps.trace.emit({
      type: "model.requested",
      stage: "model.request",
      status: "started",
      caseId,
      reasonCode: explicitSummarySource(text) ? "summarize" : "direct_answer",
    });
    const started = this.deps.clock.now().getTime();
    try {
      const generated = await draftAskProse({
        model: this.deps.model,
        ask: text,
        signal,
        caseId,
      });
      if (signal.aborted) {
        const durationMs = Math.max(0, this.deps.clock.now().getTime() - started);
        await this.deps.trace.emit({
          type: "model.failed",
          stage: "model.response",
          status: "failed",
          caseId,
          reasonCode: "cancelled",
          durationMs,
        });
        return { ok: false, failureReason: "cancelled" };
      }
      const durationMs = Math.max(0, this.deps.clock.now().getTime() - started);
      if (generated.ok) {
        await this.deps.trace.emit({
          type: "model.completed",
          stage: "model.response",
          status: "completed",
          caseId,
          reasonCode: "completed",
          durationMs,
        });
        return { ok: true, text: generated.text };
      }
      await this.deps.trace.emit({
        type: "model.failed",
        stage: "model.response",
        status: "failed",
        caseId,
        reasonCode: generated.reason === "cancelled" ? "cancelled" : "model_unavailable",
        durationMs,
      });
      return {
        ok: false,
        failureReason: generated.reason === "cancelled" ? "cancelled" : "model_unavailable",
      };
    } catch {
      const durationMs = Math.max(0, this.deps.clock.now().getTime() - started);
      await this.deps.trace.emit({
        type: "model.failed",
        stage: "model.response",
        status: "failed",
        caseId,
        reasonCode: signal.aborted ? "cancelled" : "model_unavailable",
        durationMs,
      });
      return { ok: false, failureReason: signal.aborted ? "cancelled" : "model_unavailable" };
    }
  }

  private async loadText(item: WorkItem): Promise<string | null> {
    const artifactId = String(item.payload.textArtifactId ?? "");
    const sha256 = String(item.payload.textSha256 ?? "");
    if (!artifactId || !sha256) return null;
    const raw = await this.deps.artifacts.get({ artifactId, sha256, policy: localOnlyPolicy() });
    return new TextDecoder().decode(raw);
  }

  private async personId(displayName: string): Promise<string> {
    const normal = displayName.normalize("NFKC").trim().toLocaleLowerCase();
    const digest = await sha256Hex(encodeText(normal));
    return `person_${digest.slice(0, 16)}`;
  }

  private async finishObservedCase(
    caseId: string,
    caseVersion: number,
    item: WorkItem,
    text: string,
    origin: string,
  ): Promise<WorkDisposition> {
    if (origin !== "observed") {
      await this.deps.outcomes.finishCase(caseId, caseVersion, "completed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    await this.deps.ambient.triageObserved({
      caseId,
      caseVersion,
      sourceEventId: String(item.payload.sourceEventId ?? ""),
      text,
      textArtifactId: String(item.payload.textArtifactId ?? ""),
      textSha256: String(item.payload.textSha256 ?? ""),
    });

    const after = await this.deps.store.getCase(caseId);
    if (after?.status === "waiting" && after.waitKind === "hosted_judgment") {
      return { kind: "complete" };
    }

    await this.deps.outcomes.finishCase(
      caseId,
      after?.version ?? caseVersion,
      "completed",
      this.deps.getActiveCaseId(),
    );
    return { kind: "complete" };
  }
}

/** Keep choice labels in an artifact. The queued prompt stores only the artifact reference. */
async function sealClarificationPrompt(artifacts: ArtifactStorePort, prompt: string): Promise<string> {
  let parsed: { optionIds?: unknown };
  try {
    parsed = JSON.parse(prompt) as { optionIds?: unknown };
  } catch {
    return prompt;
  }
  if (!Array.isArray(parsed.optionIds) || parsed.optionIds.length === 0) return prompt;
  const labels = parsed.optionIds.map((label) => String(label));
  const ref = await putJsonArtifact(artifacts, labels);
  return JSON.stringify({
    ...parsed,
    optionIds: [],
    optionLabelsArtifactId: ref.artifactId,
    optionLabelsSha256: ref.sha256,
  });
}

function classifyAskText(text: string): "typed_glossary" | "typed_birthday" | "typed_acronym" | "typed_general" {
  if (parseGlossaryMeans(text)) return "typed_glossary";
  if (parseBirthdayUtterance(text)) return "typed_birthday";
  if (askedToken(text)) return "typed_acronym";
  return "typed_general";
}
