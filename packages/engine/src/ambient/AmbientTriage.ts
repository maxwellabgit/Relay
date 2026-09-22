import type {
  ActionCard,
  AmbientRoute,
  AmbientTriageScores,
  ArtifactStorePort,
  CandidateEvent,
  JudgmentPort,
  JudgmentRequest,
  SourceSliceRef,
  TextModelPort,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { extractCandidate } from "../intake/CandidateExtractor.js";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";
import type { OverlayState } from "../projections/OverlayState.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import { encodeText, type EngineTrace } from "../engine-helpers.js";
import { decideAmbientRoute, type AmbientRouteDecision } from "./route-policy.js";

export const AMBIENT_QUESTION_SET_ID = "judgment.ambient-triage";
export const AMBIENT_QUESTION_SET_VERSION = "ambient-triage@1";

export type AmbientTriageDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly model: TextModelPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly outcomes: OutcomeRecorder;
  readonly overlays: OverlayState;
  readonly trace: EngineTrace;
  readonly getAbortSignal: () => AbortSignal;
  readonly getActiveCaseId: () => string | null;
  readonly emitSnapshot: () => Promise<void>;
  readonly mode?: "live" | "recorded" | "replay";
};

export type AmbientTriageResult = {
  readonly candidate: CandidateEvent;
  readonly route: AmbientRouteDecision;
  readonly recommendationId: string | null;
};

/**
 * Parallel ambient Noul set → deterministic route policy → quiet or interrupt surfacing.
 */
export class AmbientTriage {
  constructor(private readonly deps: AmbientTriageDeps) {}

  async triageObserved(input: {
    readonly caseId: string;
    readonly caseVersion: number;
    readonly sourceEventId: string;
    readonly text: string;
    readonly textArtifactId: string;
    readonly textSha256: string;
  }): Promise<AmbientTriageResult> {
    const at = this.deps.clock.now().toISOString();
    const sourceSlice: SourceSliceRef = {
      artifactId: input.textArtifactId,
      sha256: input.textSha256,
      sourceEventId: input.sourceEventId,
      start: 0,
      end: input.text.length,
      offsetsValidated: true,
    };

    const extracted = await extractCandidate({
      text: input.text,
      isAsk: false,
      sourceSlice,
      model: this.deps.model,
      signal: this.deps.getAbortSignal(),
    });

    const candidateEventId = this.deps.ids.next("cev");
    const candidate: CandidateEvent = {
      candidateEventId,
      sourceEventId: input.sourceEventId,
      caseId: input.caseId,
      kind: extracted.invalid ? "none" : extracted.kind,
      subjectRefs: extracted.subjectRefs,
      sourceSliceRefs: [sourceSlice],
      extractorVersion: extracted.extractorVersion,
      status: "new",
      createdAt: at,
      updatedAt: at,
      urgencyReason: extracted.urgencyReason,
      subjectKey: extracted.subjectKey,
    };

    await this.deps.store.putCandidateEvent(candidate);
    if (extracted.invalid) {
      await this.deps.store.appendCaseEvent(input.caseId, input.caseVersion, "candidate.invalid", at, {
        candidateEventId,
      });
      const ignored = await this.finishIgnore(candidate, {
        route: "ignore",
        reasonCode: "candidate_invalid",
        probabilities: zeroScores(),
        thresholds: {},
        quiet: true,
      });
      return ignored;
    }

    await this.deps.store.appendCaseEvent(input.caseId, input.caseVersion, "candidate.extracted", at, {
      candidateEventId,
      kind: candidate.kind,
    });

    if (candidate.kind === "none") {
      return this.finishIgnore(candidate, {
        route: "ignore",
        reasonCode: "no_candidate",
        probabilities: zeroScores(),
        thresholds: {},
        quiet: true,
      });
    }

    return this.completeTriageForCandidate({
      caseId: input.caseId,
      caseVersion: input.caseVersion,
      candidate,
      text: input.text,
      noteText: extracted.noteText ?? input.text,
      sourceSlice,
    });
  }

  /** Resume observed cases that were waiting on hosted ambient scoring. */
  async resumeHostedWaitingCases(): Promise<number> {
    if (!(await this.deps.store.getHostedProcessingEnabled())) return 0;

    const waitingCases = (await this.deps.store.listActiveCases()).filter(
      (record) => record.status === "waiting" && record.waitKind === "hosted_judgment",
    );
    let resumed = 0;

    for (const caseRecord of waitingCases) {
      const pending = (await this.deps.store.listCandidateEvents(caseRecord.caseId)).filter(
        (event) => event.status === "new",
      );
      for (const candidate of pending) {
        const slice = candidate.sourceSliceRefs[0];
        if (!slice) continue;

        const raw = await this.deps.artifacts.get({
          artifactId: slice.artifactId,
          sha256: slice.sha256,
          policy: localOnlyPolicy(),
        });
        const text = new TextDecoder().decode(raw);
        const extracted = await extractCandidate({
          text,
          isAsk: false,
          sourceSlice: slice,
          model: this.deps.model,
          signal: this.deps.getAbortSignal(),
        });
        const at = this.deps.clock.now().toISOString();

        const reactivated = await this.deps.store.updateCase(caseRecord.caseId, caseRecord.version, {
          phase: "judge",
          status: "active",
          waitKind: null,
          at,
        });
        const activeCase = reactivated ?? (await this.deps.store.getCase(caseRecord.caseId));
        if (!activeCase) continue;

        await this.completeTriageForCandidate({
          caseId: activeCase.caseId,
          caseVersion: activeCase.version,
          candidate,
          text,
          noteText: extracted.noteText ?? text,
          sourceSlice: slice,
        });

        const after = await this.deps.store.getCase(activeCase.caseId);
        if (after?.status !== "waiting" || after.waitKind !== "hosted_judgment") {
          await this.deps.outcomes.finishCase(
            activeCase.caseId,
            after?.version ?? activeCase.version,
            "completed",
            this.deps.getActiveCaseId(),
          );
        }
        resumed += 1;
      }
    }

    if (resumed > 0) await this.deps.emitSnapshot();
    return resumed;
  }

  private async completeTriageForCandidate(input: {
    readonly caseId: string;
    readonly caseVersion: number;
    readonly candidate: CandidateEvent;
    readonly text: string;
    readonly noteText: string;
    readonly sourceSlice: SourceSliceRef;
  }): Promise<AmbientTriageResult> {
    const at = this.deps.clock.now().toISOString();
    const { candidate, caseId, caseVersion, text, noteText, sourceSlice } = input;

    const suppressed =
      candidate.subjectKey != null
        ? await this.deps.store.isAmbientSuppressed(candidate.subjectKey)
        : false;

    const scored = await this.scoreAmbient(caseId, caseVersion, text, sourceSlice);
    if (!scored.ok) {
      if (scored.waitHosted) {
        await this.deps.store.updateCandidateEventStatus(candidate.candidateEventId, "new", at);
        await this.deps.store.updateCase(caseId, caseVersion, {
          phase: "judge",
          status: "waiting",
          waitKind: "hosted_judgment",
          at,
        });
        await this.deps.trace.emit({
          type: "judgment.requested",
          stage: "judgment.request",
          status: "waiting",
          caseId,
          reasonCode: "hosted_processing_disabled",
        });
        return {
          candidate,
          route: {
            route: "ignore",
            reasonCode: "waiting_hosted_judgment",
            probabilities: zeroScores(),
            thresholds: {},
            quiet: true,
          },
          recommendationId: null,
        };
      }
      return this.finishIgnore(candidate, {
        route: "ignore",
        reasonCode: scored.reasonCode,
        probabilities: zeroScores(),
        thresholds: {},
        quiet: true,
      });
    }

    const activeCases = await this.deps.store.listActiveCases();
    const hasActiveRelatedCase = activeCases.some(
      (record) => record.caseId !== caseId && (record.status === "active" || record.status === "waiting"),
    );

    const decision = decideAmbientRoute({
      scores: scored.scores,
      kind: candidate.kind,
      urgencyReason: candidate.urgencyReason ?? null,
      suppressed,
      hasActiveRelatedCase,
    });

    if (decision.route === "ignore") {
      return this.finishIgnore(candidate, decision);
    }

    await this.putTriageReceipt(caseId, decision);
    return this.surface(candidate, decision, noteText, caseId);
  }

  async acceptRecommendation(recommendationId: string): Promise<{ ok: boolean; summary: string }> {
    const cards = await this.deps.overlays.listActions();
    const card = cards.find(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );
    if (!card) return { ok: false, summary: "not_found" };
    const at = this.deps.clock.now().toISOString();
    const noteKey = card.noteKey ?? card.candidateEventId ?? recommendationId;
    const noteText = card.noteText ?? card.title ?? card.label;
    const primary = card.primary ?? "save";

    const noteStatus =
      primary === "create_task"
        ? "task"
        : primary === "verify"
          ? "verify_pending"
          : primary === "review"
            ? "reviewed"
            : "confirmed";

    const existing = await this.deps.store.learning.getMemory("note", noteKey);
    if (primary === "save" || primary === "review" || primary === "verify" || primary === "create_task") {
      await this.deps.store.learning.putMemory({
        memoryId: existing?.memoryId ?? this.deps.ids.next("mem"),
        kind: "note",
        key: noteKey,
        value: {
          text: noteText,
          status: noteStatus,
          ...(card.candidateEventId ? { candidateEventId: card.candidateEventId } : {}),
        },
        source: "explicit_user",
        createdAt: existing?.createdAt ?? at,
      });
    }

    if (card.candidateEventId) {
      await this.deps.store.updateCandidateEventStatus(card.candidateEventId, "resolved", at);
    }

    await this.deps.overlays.clearActions(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );

    const feedSummary =
      primary === "create_task"
        ? `Task noted: ${noteText}`
        : primary === "verify"
          ? `Verify: ${noteText}`
          : primary === "review"
            ? `Reviewed: ${noteText}`
            : `Saved: ${noteText}`;
    const feedKind = primary === "create_task" || primary === "review" ? "task" : "memory";

    await this.deps.outcomes.publishFeedItem({
      itemId: this.deps.ids.next("feed"),
      kind: feedKind,
      summary: feedSummary,
      createdAt: at,
      ...(card.caseId ? { caseId: card.caseId } : {}),
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: "accepted" };
  }

  async dismissRecommendation(recommendationId: string): Promise<{ ok: boolean; summary: string }> {
    const at = this.deps.clock.now().toISOString();
    const cards = await this.deps.overlays.listActions();
    const card = cards.find(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );
    await this.deps.overlays.clearActions(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );
    if (card?.candidateEventId) {
      await this.deps.store.updateCandidateEventStatus(card.candidateEventId, "resolved", at);
    }
    await this.deps.emitSnapshot();
    return { ok: true, summary: "dismissed" };
  }

  async feedbackRecommendation(
    recommendationId: string,
    feedback: "not_useful" | "never_for_project" | "why",
  ): Promise<{ ok: boolean; summary: string }> {
    const cards = await this.deps.overlays.listActions();
    const card = cards.find(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );
    if (!card) return { ok: false, summary: "not_found" };
    const at = this.deps.clock.now().toISOString();
    if (feedback === "never_for_project" || feedback === "not_useful") {
      const key = card.noteKey ?? card.candidateEventId ?? recommendationId;
      await this.deps.store.putAmbientSuppression(key, feedback, at);
    }
    await this.deps.overlays.clearActions(
      (c) => c.kind === "ambient_recommendation" && c.recommendationId === recommendationId,
    );
    if (card.candidateEventId) {
      await this.deps.store.updateCandidateEventStatus(card.candidateEventId, "resolved", at);
    }
    await this.deps.outcomes.putReceipt({
      caseId: card.caseId ?? null,
      gateId: "ambient.feedback",
      policyVersion: AMBIENT_QUESTION_SET_VERSION,
      questionType: "user",
      provider: "user",
      probabilities: {},
      thresholds: {},
      selectedOption: feedback,
      result: "pass",
      reasonCode: feedback,
      latencyMs: null,
    });
    await this.deps.emitSnapshot();
    return { ok: true, summary: feedback };
  }

  private async scoreAmbient(
    caseId: string,
    caseVersion: number,
    text: string,
    sourceSlice: SourceSliceRef,
  ): Promise<
    | { ok: true; scores: AmbientTriageScores }
    | { ok: false; reasonCode: string; waitHosted?: boolean }
  > {
    const sourceBytes = encodeText(text);
    const sourceRef = await this.deps.artifacts.put(sourceBytes, localOnlyPolicy());
    const request: JudgmentRequest = {
      questionSetId: AMBIENT_QUESTION_SET_ID,
      questionSetVersion: AMBIENT_QUESTION_SET_VERSION,
      model: "typesafe",
      provider: "typesafe",
      caseId,
      caseVersion,
      state: { origin: "observed" },
      sourceObjectRefs: [{ artifactId: sourceRef.artifactId, sha256: sourceRef.sha256 }],
      questions: {
        worth_remembering: {
          type: "noul",
          instructions: "Is this worth remembering as durable project information?",
        },
        possible_fact_claim: {
          type: "noul",
          instructions: "Is this a checkable factual claim?",
        },
        possible_correction: {
          type: "noul",
          instructions: "Is this correcting prior information?",
        },
        possible_commitment: {
          type: "noul",
          instructions: "Is this a commitment or next-task obligation?",
        },
        possible_open_question: {
          type: "noul",
          instructions: "Is this an unresolved open question?",
        },
        related_to_active_case: {
          type: "noul",
          instructions: "Is this related to an already active Case?",
        },
        interrupt_worthy: {
          type: "noul",
          instructions: "Should this interrupt the calm feed right now?",
        },
      },
    };

    const { response } = await runJudgmentLifecycle(
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

    if (!response.ok) {
      if (
        response.failure.category === "disabled" ||
        response.failure.category === "not_authorized"
      ) {
        return { ok: false, reasonCode: response.failure.category, waitHosted: true };
      }
      return { ok: false, reasonCode: response.failure.category };
    }

    const answers = response.success.answers;
    const scores: AmbientTriageScores = {
      worth_remembering: noul(answers.worth_remembering),
      possible_fact_claim: noul(answers.possible_fact_claim),
      possible_correction: noul(answers.possible_correction),
      possible_commitment: noul(answers.possible_commitment),
      possible_open_question: noul(answers.possible_open_question),
      related_to_active_case: noul(answers.related_to_active_case),
      interrupt_worthy: noul(answers.interrupt_worthy),
    };
    void sourceSlice;
    return { ok: true, scores };
  }

  private async putTriageReceipt(caseId: string, decision: AmbientRouteDecision): Promise<void> {
    await this.deps.outcomes.putReceipt({
      caseId,
      gateId: "ambient.triage",
      policyVersion: AMBIENT_QUESTION_SET_VERSION,
      questionType: "noul",
      provider: "typesafe",
      probabilities: decision.probabilities,
      thresholds: decision.thresholds,
      selectedOption: decision.route,
      selectedOptionId: decision.route,
      optionLabels: Object.fromEntries(
        (Object.keys(decision.probabilities) as (keyof AmbientTriageScores)[]).map((k) => [k, k]),
      ),
      result: "pass",
      reasonCode: decision.reasonCode,
      latencyMs: null,
    });
  }

  private async finishIgnore(
    candidate: CandidateEvent,
    decision: AmbientRouteDecision,
  ): Promise<AmbientTriageResult> {
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.updateCandidateEventStatus(candidate.candidateEventId, "ignored", at);
    await this.putTriageReceipt(candidate.caseId, decision);
    await this.deps.trace.emit({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId: candidate.caseId,
      reasonCode: "ambient_ignore",
    });
    return { candidate, route: decision, recommendationId: null };
  }

  private async surface(
    candidate: CandidateEvent,
    decision: AmbientRouteDecision,
    noteText: string,
    caseId: string,
  ): Promise<AmbientTriageResult> {
    const at = this.deps.clock.now().toISOString();
    const recommendationId = this.deps.ids.next("rec");
    const primary = primaryForRoute(decision.route);
    const title = titleForRoute(decision.route, noteText);
    const reason = reasonLine(decision.reasonCode, candidate.kind);
    const deduped = await this.dedupeNote(candidate.subjectKey, noteText);
    if (deduped) {
      await this.deps.store.updateCandidateEventStatus(candidate.candidateEventId, "ignored", at);
      return {
        candidate,
        route: { ...decision, route: "ignore", reasonCode: "deduped" },
        recommendationId: null,
      };
    }

    const card: ActionCard = {
      actionId: this.deps.ids.next("act"),
      kind: "ambient_recommendation",
      label: primary === "save" ? "Save" : primary === "verify" ? "Verify" : primary === "create_task" ? "Create task" : "Review",
      recommendationId,
      candidateEventId: candidate.candidateEventId,
      caseId,
      title,
      reason,
      evidenceCount: candidate.sourceSliceRefs.length,
      primary,
      quiet: decision.quiet && decision.route !== "interrupt",
      noteKey: candidate.subjectKey ?? candidate.candidateEventId,
      noteText,
    };

    await this.deps.overlays.stageAction(card);
    if (decision.route === "interrupt") {
      await this.deps.outcomes.publishFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "task",
        summary: title,
        createdAt: at,
        caseId,
      });
    } else {
      await this.deps.outcomes.publishFeedItem({
        itemId: this.deps.ids.next("feed"),
        kind: "task",
        summary: title,
        createdAt: at,
        caseId,
      });
    }

    await this.deps.store.updateCandidateEventStatus(candidate.candidateEventId, "surfaced", at);
    await this.deps.trace.emit({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId,
      reasonCode: `ambient_${decision.route}`,
    });
    await this.deps.emitSnapshot();
    return { candidate, route: decision, recommendationId };
  }

  private async dedupeNote(subjectKey: string | null | undefined, noteText: string): Promise<boolean> {
    if (!subjectKey) return false;
    const existing = await this.deps.store.learning.getMemory("note", subjectKey);
    if (!existing?.value.text) return false;
    return normalize(existing.value.text) === normalize(noteText);
  }
}

function noul(answer: { type: string; probabilityYes?: number } | undefined): number {
  if (!answer || answer.type !== "noul" || typeof answer.probabilityYes !== "number") return 0;
  return answer.probabilityYes;
}

function zeroScores(): AmbientTriageScores {
  return {
    worth_remembering: 0,
    possible_fact_claim: 0,
    possible_correction: 0,
    possible_commitment: 0,
    possible_open_question: 0,
    related_to_active_case: 0,
    interrupt_worthy: 0,
  };
}

function primaryForRoute(route: AmbientRoute): "save" | "verify" | "create_task" | "review" {
  switch (route) {
    case "verify_claim":
      return "verify";
    case "suggest_next_task":
      return "create_task";
    case "interrupt":
      return "review";
    default:
      return "save";
  }
}

function titleForRoute(route: AmbientRoute, noteText: string): string {
  const short = noteText.length > 72 ? `${noteText.slice(0, 69)}…` : noteText;
  switch (route) {
    case "propose_note":
      return `Save this? ${short}`;
    case "verify_claim":
      return `Verify this claim? ${short}`;
    case "suggest_next_task":
      return `Create a task? ${short}`;
    case "attach_evidence":
      return `Attach to active work? ${short}`;
    case "interrupt":
      return `Needs attention: ${short}`;
    default:
      return short;
  }
}

function reasonLine(reasonCode: string, kind: string): string {
  switch (reasonCode) {
    case "worth_remembering":
      return "Looks like durable information worth keeping.";
    case "correction":
      return "Sounds like a correction to saved information.";
    case "fact_claim":
      return "This looks like a checkable factual claim.";
    case "commitment":
      return "This looks like a commitment or next step.";
    case "open_question":
      return "An open question was noticed.";
    case "related_active_case":
      return "Related to work already in progress.";
    default:
      if (reasonCode.startsWith("urgency:")) return "Flagged as time-sensitive.";
      return `Noticed as ${kind.replace(/_/g, " ")}.`;
  }
}

function normalize(text: string): string {
  return text.normalize("NFKC").toLowerCase().replace(/\s+/g, " ").trim();
}
