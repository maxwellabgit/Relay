import type { JudgmentRequest, RelayCommandResult } from "@relay/contracts";
import type { EpisodeDefinition } from "../episodes.js";
import { inspectRuntime } from "../inspect.js";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import {
  BENEFIT_YES_MINIMUM,
  patternReady,
  RETENTION_LABEL,
  type EpisodeOutcome,
  type ReceiptRecord,
} from "../learning-store.js";
import { knownReason } from "../runtime-events.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort, JudgmentPort } from "@relay/contracts";
import type { EngineTrace } from "../engine-helpers.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";

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
    await this.deps.store.learning.putCandidate({
      ...current,
      state,
      needed: "",
      updatedAt: this.deps.clock.now().toISOString(),
    });
    await this.deps.trace.emit({
      type: state === "approved" ? "candidate.approved" : "candidate.rejected",
      stage: "proposal.create",
      status: "completed",
      reasonCode,
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: state };
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
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
      },
      request,
      this.deps.getAbortSignal(),
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
