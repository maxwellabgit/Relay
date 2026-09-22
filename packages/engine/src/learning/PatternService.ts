import type { JudgmentRequest, RelayCommandResult } from "@relay/contracts";
import type { EpisodeDefinition } from "../episodes.js";
import { inspectRuntime } from "../inspect.js";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import {
  BENEFIT_YES_MINIMUM,
  patternReady,
  RETENTION_LABEL,
  type CandidateBuildMeta,
  type CandidateState,
  type EpisodeOutcome,
  type PatternEvidenceRecord,
  type ReceiptRecord,
} from "../learning-store.js";
import { knownReason } from "../runtime-events.js";
import { JEV_MODEL } from "../typesafe-judgment.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort, JudgmentPort } from "@relay/contracts";
import type { EngineTrace } from "../engine-helpers.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";
import type { AuthorityState } from "../operations/AuthorityState.js";
import {
  buildReflexFromTemplate,
  evaluateShadow,
  isLearnedTemplateReflexId,
  templateIdForSignature,
} from "./reflex-templates.js";

export type PatternServiceDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly mode?: "live" | "recorded" | "replay";
  readonly gitCommit?: string;
  readonly storageDetail?: string;
  readonly episodeDefinitions?: readonly EpisodeDefinition[];
  readonly outcomes: OutcomeRecorder;
  readonly trace: EngineTrace;
  readonly getAbortSignal: () => AbortSignal;
  readonly setActiveEpisodeId: (id: string | null) => void;
  readonly emitSnapshot: () => Promise<void>;
  readonly runId: () => string;
  readonly authority?: AuthorityState;
};

export class PatternService {
  constructor(private readonly deps: PatternServiceDeps) {}

  async setCandidateState(
    candidateId: string,
    state: "approved" | "rejected" | "snoozed",
    reasonCode: "user_approval" | "user_reject" | "user_snooze",
  ): Promise<RelayCommandResult> {
    const candidates = await this.deps.store.learning.listCandidates();
    const current = candidates.find((candidate) => candidate.candidateId === candidateId);
    if (!current || current.state !== "proposed") return { ok: false, summary: "not_proposed" };

    if (state === "approved") {
      return this.approveForBuild(current.candidateId);
    }

    await this.deps.store.learning.putCandidate({
      ...current,
      state,
      needed: "",
      updatedAt: this.deps.clock.now().toISOString(),
    });
    await this.deps.trace.emit({
      type: "candidate.rejected",
      stage: "proposal.create",
      status: "completed",
      reasonCode,
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: state };
  }

  /**
   * Approval at proposed authorizes building only a template-bounded definition.
   * Runs shadow evaluation and stops at activation_ready — never auto-activates.
   */
  async approveForBuild(
    candidateId: string,
    reasonCode: "user_approval" = "user_approval",
  ): Promise<RelayCommandResult> {
    const candidates = await this.deps.store.learning.listCandidates();
    const current = candidates.find((candidate) => candidate.candidateId === candidateId);
    if (!current || current.state !== "proposed") return { ok: false, summary: "not_proposed" };

    const templateId = templateIdForSignature(current.signature);
    if (!templateId) return { ok: false, summary: "no_template" };

    const at = this.deps.clock.now().toISOString();
    await this.deps.store.learning.putCandidate({
      ...current,
      state: "approved_for_build",
      needed: "build",
      updatedAt: at,
    });

    const version = 1;
    const definition = buildReflexFromTemplate({
      templateId,
      signature: current.signature,
      version,
    });
    const pattern = await this.deps.store.learning.getPattern(current.signature);
    const shadow = evaluateShadow(definition, current.signature, pattern?.count ?? 0, at);
    if (!shadow.pass) {
      await this.deps.store.learning.putCandidate({
        ...current,
        state: "built",
        needed: "shadow_failed",
        updatedAt: this.deps.clock.now().toISOString(),
        meta: {
          reflexId: definition.id,
          reflexVersion: definition.version,
          templateId,
          shadowPass: false,
          shadowReportJson: JSON.stringify(shadow),
        },
      });
      await this.deps.emitSnapshot();
      return { ok: false, summary: "shadow_failed" };
    }

    const meta: CandidateBuildMeta = {
      reflexId: definition.id,
      reflexVersion: definition.version,
      templateId,
      shadowPass: true,
      shadowReportJson: JSON.stringify(shadow),
      priorActivation: "inactive",
    };
    // Persist activation_ready + meta before authority state so restarts cannot bypass the gate.
    await this.deps.store.learning.putCandidate({
      ...current,
      state: "activation_ready",
      needed: "",
      because: current.because,
      updatedAt: this.deps.clock.now().toISOString(),
      meta,
    });

    if (this.deps.authority) {
      await this.deps.authority.setReflexState(
        {
          reflex: { id: definition.id, version: definition.version },
          stateVersion: 1,
          activation: "inactive",
          runs: { runCount: 0, successCount: 0, failureCount: 0 },
        },
        at,
      );
    }

    await this.deps.trace.emit({
      type: "candidate.approved",
      stage: "proposal.create",
      status: "completed",
      reasonCode,
    });
    await this.deps.emitSnapshot();
    return {
      ok: true,
      summary: "activation_ready",
      reflexId: definition.id,
      reflexVersion: definition.version,
    };
  }

  async canActivateBuiltReflex(
    reflexId: string,
    reflexVersion: number,
  ): Promise<{ ok: true } | { ok: false; summary: string }> {
    const candidates = await this.deps.store.learning.listCandidates();
    const match = candidates.find(
      (c) => c.meta?.reflexId === reflexId && c.meta?.reflexVersion === reflexVersion,
    );
    if (match) {
      if (match.state === "activation_ready" || match.state === "active" || match.state === "paused") {
        return { ok: true };
      }
      return { ok: false, summary: "not_activation_ready" };
    }
    // Template-learned reflexes must never activate without an activation_ready candidate.
    if (isLearnedTemplateReflexId(reflexId)) {
      return { ok: false, summary: "not_activation_ready" };
    }
    return { ok: true }; // production/builtin reflexes without candidate rows
  }

  async markCandidateActivation(
    reflexId: string,
    reflexVersion: number,
    activation: "active" | "paused" | "rolled_back",
  ): Promise<void> {
    const candidates = await this.deps.store.learning.listCandidates();
    const match = candidates.find(
      (c) => c.meta?.reflexId === reflexId && c.meta?.reflexVersion === reflexVersion,
    );
    if (!match) return;
    const state: CandidateState =
      activation === "active" ? "active" : activation === "paused" ? "paused" : "rolled_back";
    await this.deps.store.learning.putCandidate({
      ...match,
      state,
      updatedAt: this.deps.clock.now().toISOString(),
      ...(match.meta
        ? {
            meta: {
              ...match.meta,
              priorActivation: activation === "rolled_back" ? "inactive" : activation,
            },
          }
        : {}),
    });
  }

  async recordPatternEvidence(
    input: Omit<PatternEvidenceRecord, "evidenceId" | "createdAt"> & { evidenceId?: string },
  ): Promise<void> {
    const record: PatternEvidenceRecord = {
      evidenceId: input.evidenceId ?? this.deps.ids.next("pev"),
      signature: input.signature,
      sourceClass: input.sourceClass,
      routeOrTool: input.routeOrTool,
      userAction: input.userAction,
      outcomeClass: input.outcomeClass,
      duplicateCount: input.duplicateCount,
      timeToActionMs: input.timeToActionMs,
      feedback: input.feedback,
      caseId: input.caseId,
      createdAt: this.deps.clock.now().toISOString(),
    };
    await this.deps.store.learning.putPatternEvidence(record);
  }

  async startWorkSession(): Promise<RelayCommandResult> {
    const open = await this.deps.store.learning.currentSession();
    if (open) return { ok: false, summary: "already_open" };
    await this.openWorkSession();
    await this.deps.emitSnapshot();
    return { ok: true, summary: "started" };
  }

  async endWorkSession(): Promise<RelayCommandResult> {
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
    await this.deps.trace.emit({
      type: "session.ended",
      reasonCode: termination,
      selectedOutcome: String(episodes.length),
    });
    await this.deps.emitSnapshot();
    return { ok: termination === "completed", summary: termination };
  }

  async openWorkSession(): Promise<string> {
    const sessionId = this.deps.ids.next("work");
    await this.deps.store.learning.openSession({
      sessionId,
      startedAt: this.deps.clock.now().toISOString(),
      endedAt: null,
      termination: "open",
      episodeCount: 0,
    });
    await this.deps.trace.emit({ type: "session.started", reasonCode: "open", selectedOutcome: sessionId });
    return sessionId;
  }

  async recordEpisode(signature: string, caseId: string | null, outcome: EpisodeOutcome): Promise<void> {
    const open = (await this.deps.store.learning.currentSession()) ?? {
      sessionId: await this.openWorkSession(),
    };
    const episodeId = caseId ? `episode_${caseId}` : this.deps.ids.next("episode");
    const at = this.deps.clock.now().toISOString();
    const record = {
      episodeId,
      sessionId: open.sessionId,
      caseId,
      signature,
      outcome,
      startedAt: at,
      completedAt: at,
    };
    let pattern = null;
    if (outcome === "completed") {
      pattern = await this.deps.store.learning.recordCompletedEpisode(record);
      await this.recordPatternEvidence({
        signature,
        sourceClass: "verified_work",
        routeOrTool: signature.split("|")[0] ?? null,
        userAction: "completed",
        outcomeClass: "completed",
        duplicateCount: 0,
        timeToActionMs: null,
        feedback: null,
        caseId,
      });
      if (pattern && patternReady(pattern)) {
        await this.considerCandidate(pattern.signature, pattern.count, pattern.sessionIds.length);
      }
    } else {
      await this.deps.store.learning.putEpisode(record);
    }
    this.deps.setActiveEpisodeId(outcome === "completed" ? null : episodeId);
    await this.deps.trace.emit({
      type: "episode.recorded",
      stage: "episode.complete",
      status: "completed",
      ...(caseId ? { caseId } : {}),
      episodeId,
      reasonCode: outcome === "completed" ? "completed" : "unresolved",
    });
  }

  async completeVerifiedWork(
    kind: string,
    fields: Readonly<Record<string, string>>,
  ): Promise<RelayCommandResult> {
    const definition = this.deps.episodeDefinitions?.find((item) => item.kind === kind);
    if (!definition?.candidateTemplateId && kind !== definition?.kind) return { ok: false, summary: "no_definition" };
    if (!definition) return { ok: false, summary: "no_definition" };
    const signature = definition.normalize(fields);
    if (!signature) return { ok: false, summary: "invalid_signature" };
    await this.deps.outcomes.putReceipt({
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
    await this.deps.emitSnapshot();
    return { ok: true, summary: "episode_recorded" };
  }

  async considerCandidate(signature: string, count: number, sessions: number): Promise<void> {
    const candidateId = `cand_${signature}`;
    const existing = (await this.deps.store.learning.listCandidates()).find(
      (candidate) => candidate.candidateId === candidateId,
    );
    // Exactly one reviewable proposal — never multiply or undo user decisions.
    // Only `qualified` (benefit pending/failed) may be re-judged.
    if (existing && existing.state !== "qualified") {
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
      model: JEV_MODEL,
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
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
      },
      request,
      this.deps.getAbortSignal(),
    );
    let state: "qualified" | "proposed" = "qualified";
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
    await this.deps.outcomes.putReceipt({
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

  async maybeReview(): Promise<void> {
    const deadLetters = (await this.deps.store.listDeadLetters()).length;
    const inspected = await inspectRuntime(this.deps.store.learning, [], {
      runId: this.deps.runId(),
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
    await this.deps.trace.emit({
      type: "review.created",
      stage: "review.evaluate",
      status: "completed",
      reasonCode: knownReason(inspected.review.trigger),
    });
  }
}
