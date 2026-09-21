import type { JudgmentRequest } from "@relay/contracts";
import { isRetryableJudgmentFailure } from "@relay/contracts";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import { workSignature } from "../learning-store.js";
import { evaluateChoiceGate, validateChoiceDistribution } from "../policies.js";
import { JUDGMENT_MAX_ATTEMPTS, backoffMs, knownReason } from "../runtime-events.js";
import type { WorkItem } from "../queue.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort, JudgmentPort } from "@relay/contracts";
import { feedItemId, type EngineTrace } from "../engine-helpers.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";
import type { OverlayState } from "../projections/OverlayState.js";
import type { PatternService } from "../learning/PatternService.js";

export type WorkDisposition =
  | { readonly kind: "complete" }
  | { readonly kind: "retry"; readonly reasonCode: string; readonly delayMs: number; readonly payload: Record<string, unknown> }
  | { readonly kind: "dead"; readonly reasonCode: string };

export type JudgmentServiceDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly mode?: "live" | "recorded" | "replay";
  readonly outcomes: OutcomeRecorder;
  readonly overlays: OverlayState;
  readonly patterns: PatternService;
  readonly trace: EngineTrace;
  readonly getAbortSignal: () => AbortSignal;
  readonly getActiveCaseId: () => string | null;
  readonly offerGlossary: (token: string, expansion: string) => Promise<void>;
};

export class JudgmentService {
  constructor(private readonly deps: JudgmentServiceDeps) {}

  async onJudgmentRequested(item: WorkItem): Promise<WorkDisposition> {
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
    let parsed: {
      optionIds?: string[];
      token?: string;
      choiceProbabilityMinimum?: number;
      choiceMarginMinimum?: number;
    } = {};
    try {
      parsed = JSON.parse(String(item.payload.prompt ?? "")) as typeof parsed;
    } catch {
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
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
        useful: {
          type: "noul",
          instructions: "Is showing an expansion for this acronym useful in the current conversational context?",
        },
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
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
      },
      request,
      this.deps.getAbortSignal(),
    );
    const durationMs = Date.now() - started;
    const requestedAt = new Date(started).toISOString();
    const completedAt = this.deps.clock.now().toISOString();
    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      const reasonCode =
        outcome.response.failure.message === "hosted_processing_disabled"
          ? knownReason("hosted_processing_disabled")
          : knownReason(category);
      const retrying = isRetryableJudgmentFailure(category) && attempt < JUDGMENT_MAX_ATTEMPTS;
      const blocked =
        category === "missing_secret" ||
        category === "authentication" ||
        category === "disabled" ||
        category === "not_authorized";
      await this.deps.outcomes.putReceipt({
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
        await this.deps.trace.emit({
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
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "wait"),
        kind: "wait",
        summary: `Jev choice unavailable · ${reasonCode}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, status, this.deps.getActiveCaseId());
      await this.deps.trace.emit({
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
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
      return { kind: "dead", reasonCode: "invalid_response" };
    }
    const probabilities: Record<string, number> = {};
    for (const [key, value] of Object.entries(answer.probabilities)) {
      const id = idByLabel.get(key) ?? (key === "no_match" || optionIds.includes(key) ? key : "");
      if (!id) {
        await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
        return { kind: "dead", reasonCode: "not_in_options" };
      }
      probabilities[id] = value;
    }
    const declared = idByLabel.get(answer.choice) ?? answer.choice;
    const distribution = validateChoiceDistribution({ probabilities, declared, allowed: optionIds });
    if (!distribution.ok) {
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
      return { kind: "dead", reasonCode: knownReason(distribution.reasonCode) };
    }
    const explicitAsk = item.payload.explicitAsk === true;
    const usefulnessMinimum = Number(
      (parsed as { displayUsefulnessMinimum?: number }).displayUsefulnessMinimum ?? 0.7,
    );
    const gate = evaluateChoiceGate({ probabilities, minimum, marginMinimum });
    let pass = gate.pass && gate.selected === declared && gate.selected !== "no_match";
    let reasonCode = pass ? "policy_pass" : knownReason(gate.reasonCode);
    if (pass && !explicitAsk) {
      const useful = outcome.response.success.answers.useful;
      if (!useful || useful.type !== "noul" || useful.probabilityYes < usefulnessMinimum) {
        pass = false;
        reasonCode = "below_usefulness";
      }
    }
    const label = pass ? (labelById[gate.selected!] ?? "") : "";
    await this.deps.outcomes.putReceipt({
      caseId,
      gateId: reflexId,
      policyVersion: "resolve-acronym@1",
      questionType: "choice",
      provider: request.provider ?? "unknown",
      probabilities,
      thresholds: {
        choiceProbabilityMinimum: minimum,
        choiceMarginMinimum: marginMinimum,
        displayUsefulnessMinimum: usefulnessMinimum,
      },
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
    await this.deps.trace.emit({
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
    if (pass && label) {
      const token = String(item.payload.token ?? "");
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "answer"),
        kind: "answer",
        summary: label,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.trace.emit({
        type: "answer.committed",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: "policy_pass",
      });
      const signature = workSignature("acronym.lookup", { outcome: "choice", token });
      if (signature) await this.deps.patterns.recordEpisode(signature, caseId, "completed");
      await this.deps.offerGlossary(token, label);
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
    } else {
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
    }
    return { kind: "complete" };
  }
}
