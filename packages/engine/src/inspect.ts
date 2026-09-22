import type {
  CaseExecutionView,
  DecisionReceiptView,
  DecisionRunView,
  MemoryView,
  PatternView,
  ReviewStatus,
  RuntimeHeader,
  TraceRow,
} from "@relay/contracts";
import {
  PATTERN_EPISODE_MINIMUM,
  REVIEW_CANDIDATE_TRIGGER,
  REVIEW_EPISODE_TRIGGER,
  REVIEW_REFLEX_TRIGGER,
  REVIEW_SESSION_TRIGGER,
  reviewTrigger,
  type LearningStore,
  type ReceiptRecord,
} from "./learning-store.js";
import { labelsOrUnavailable } from "./protected-content.js";
import { projectCaseExecution, selectCaseIdForExecution } from "./project-case-execution.js";
import { projectDecisionRun, selectReceiptForDecision } from "./project-decision-run.js";
import type { RuntimeEventV2 } from "./runtime-events.js";

export async function inspectRuntime(
  learning: LearningStore,
  traces: readonly RuntimeEventV2[],
  header: Omit<RuntimeHeader, "sessionId">,
  caseStatusById: ReadonlyMap<string, "active" | "waiting" | "completed" | "blocked" | "failed"> = new Map(),
): Promise<{
  runtime: RuntimeHeader;
  gate: DecisionReceiptView | null;
  decision: DecisionRunView | null;
  caseExecution: CaseExecutionView | null;
  patterns: readonly PatternView[];
  review: ReviewStatus;
  trace: readonly TraceRow[];
  memories: readonly MemoryView[];
}> {
  const [sessions, episodes, receipts, patterns, candidates, memories, reviews] = await Promise.all([
    learning.listSessions(),
    learning.listEpisodes(),
    learning.listReceipts(),
    learning.listPatterns(),
    learning.listCandidates(),
    learning.listMemories(),
    learning.listReviews(),
  ]);
  const open = sessions.find((session) => session.termination === "open") ?? null;
  const completeSessions = sessions.filter(
    (session) => session.termination === "completed" && session.episodeCount > 0,
  ).length;
  const completeEpisodes = episodes.filter((episode) => episode.outcome === "completed").length;
  const lastReview = reviews.at(-1) ?? null;
  const approvedCandidates = candidates.filter((candidate) =>
    ["approved", "approved_for_build", "built", "shadow", "activation_ready", "active", "paused"].includes(
      candidate.state,
    ),
  ).length;
  const builtReflexes = candidates.filter((candidate) =>
    ["built", "shadow", "activation_ready", "active", "paused", "rolled_back"].includes(candidate.state),
  ).length;
  const activeReflexes = candidates.filter((candidate) => candidate.state === "active").length;
  const qualifiedCandidates = candidates.filter(
    (candidate) =>
      candidate.state !== "observing" &&
      candidate.state !== "rejected" &&
      candidate.state !== "snoozed",
  ).length;
  const trigger = reviewTrigger({
    completeSessions: completeSessions - (lastReview?.sessionsAtReview ?? 0),
    builtReflexes: builtReflexes - (lastReview?.builtReflexesAtReview ?? 0),
    completeEpisodes: completeEpisodes - (lastReview?.episodesAtReview ?? 0),
    qualifiedCandidates: qualifiedCandidates - (lastReview?.candidatesAtReview ?? 0),
  });
  const selected = selectReceiptForDecision(receipts, header.activeCaseId);
  const decision = projectDecisionRun({
    receipt: selected.receipt,
    events: traces,
    activeCaseId: header.activeCaseId,
    historical: selected.historical,
  });
  const selectedCase = selectCaseIdForExecution(traces, header.activeCaseId);
  const caseReceipt =
    selectedCase.caseId == null
      ? null
      : ([...receipts].reverse().find((item) => item.caseId === selectedCase.caseId) ?? null);
  const caseExecution = projectCaseExecution({
    caseId: selectedCase.caseId,
    events: traces,
    receipt: caseReceipt,
    caseStatus: selectedCase.caseId ? (caseStatusById.get(selectedCase.caseId) ?? null) : null,
    historical: selectedCase.historical,
  });
  // Gate remains for migration: prefer correlated selection, else latest receipt (may lack caseId).
  const gateReceipt = selected.receipt ?? receipts.at(-1) ?? null;
  return {
    runtime: {
      ...header,
      sessionId: open?.sessionId ?? null,
    },
    gate: gateReceipt ? toGate(gateReceipt) : null,
    decision,
    caseExecution,
    patterns: patterns.map((pattern) => {
      const candidate = candidates.find((item) => item.signature === pattern.signature) ?? null;
      return {
        signature: pattern.signature,
        count: pattern.count,
        sessions: pattern.sessionIds.length,
        outcomes: pattern.outcomes,
        firstAt: pattern.firstAt,
        lastAt: pattern.lastAt,
        evidenceIds: pattern.evidenceIds,
        candidateState: candidate?.state ?? (pattern.count > 0 ? "observing" : null),
        candidateId: candidate?.candidateId ?? null,
        because: candidate?.because ?? "",
        needed:
          candidate?.needed ??
          (pattern.sessionIds.length < 2
            ? "distinct_sessions"
            : pattern.count < PATTERN_EPISODE_MINIMUM
              ? "repeated_episodes"
              : ""),
      };
    }),
    review: {
      completeSessions,
      sessionTrigger: REVIEW_SESSION_TRIGGER,
      approvedCandidates,
      builtReflexes,
      activeReflexes,
      reflexTrigger: REVIEW_REFLEX_TRIGGER,
      completeEpisodes,
      episodeTrigger: REVIEW_EPISODE_TRIGGER,
      qualifiedCandidates,
      candidateTrigger: REVIEW_CANDIDATE_TRIGGER,
      reviewDue: trigger !== null,
      trigger,
    },
    trace: traces.slice(-80).map((event) => ({
      sequence: event.sequence,
      at: event.at,
      type: event.eventType,
      stage: event.stage,
      status: event.status,
      reasonCode: event.reasonCode ?? null,
      latencyMs: event.durationMs ?? null,
      durationMs: event.durationMs ?? null,
      attempt: event.attempt ?? null,
      caseId: event.caseId ?? null,
      episodeId: event.episodeId ?? null,
      runId: event.runId,
      captureSessionId: event.captureSessionId ?? null,
      workSessionId: event.workSessionId ?? event.sessionId ?? null,
      decisionId: event.decisionId ?? null,
      judgmentId: event.judgmentId ?? null,
      receiptId: event.receiptId ?? null,
      reflexId: event.reflexId ?? null,
      result: event.status,
    })),
    memories: memories.map((memory) => ({
      kind: memory.kind,
      key: memory.key,
      fields: memory.value,
    })),
  };
}

function toGate(receipt: ReceiptRecord): DecisionReceiptView {
  const values = Object.values(receipt.probabilities);
  const top = values.length === 0 ? null : Math.max(...values);
  const ranked = Object.values(receipt.probabilities).sort((a, b) => b - a);
  const margin = ranked.length === 0 ? null : ranked.length === 1 ? ranked[0]! : ranked[0]! - ranked[1]!;
  const threshold = Object.values(receipt.thresholds)[0] ?? null;
  const elapsed =
    receipt.requestedAt && receipt.completedAt
      ? Math.max(0, Date.parse(receipt.completedAt) - Date.parse(receipt.requestedAt))
      : receipt.latencyMs;
  const optionIds = Object.keys(receipt.probabilities);
  return {
    at: receipt.createdAt,
    gateId: receipt.gateId,
    policyVersion: receipt.policyVersion,
    reflexId: receipt.reflexId ?? (receipt.gateId.startsWith("reflex.") ? receipt.gateId : null),
    questionType: receipt.questionType,
    optionIds,
    probabilities: receipt.probabilities,
    topProbability: top,
    margin,
    threshold,
    thresholds: receipt.thresholds,
    optionLabels: labelsOrUnavailable(optionIds, receipt.optionLabels),
    result: receipt.result,
    reasonCode: receipt.reasonCode,
    provider: receipt.provider,
    latencyMs: Number.isFinite(elapsed) ? elapsed : receipt.latencyMs,
    retries: receipt.retries,
    nextAction: nextAction(receipt),
    receiptId: receipt.receiptId,
    decisionId: receipt.decisionId,
    caseId: receipt.caseId,
    judgmentId: receipt.judgmentId,
    selectedOptionId: receipt.selectedOptionId ?? receipt.selectedOption,
  };
}

function nextAction(receipt: ReceiptRecord): string {
  if (receipt.result === "wait") return "bounded_wait";
  if (receipt.result === "blocked") return "blocked";
  if (receipt.result === "fail") return "no_definition";
  if (receipt.result === "not_applicable") return "local_result";
  if (receipt.questionType === "user") return "stored";
  return "continue";
}
