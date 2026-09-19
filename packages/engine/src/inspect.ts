import type {
  DecisionReceiptView,
  MemoryView,
  PatternView,
  ReviewStatus,
  RuntimeHeader,
  TraceRow,
} from "@relay/contracts";
import type { TraceEventV1 } from "@relay/contracts";
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

export async function inspectRuntime(
  learning: LearningStore,
  traces: readonly TraceEventV1[],
  header: Omit<RuntimeHeader, "queueDepth" | "sessionId" | "episodeId"> & { queueDepth: number },
): Promise<{
  runtime: RuntimeHeader;
  gate: DecisionReceiptView | null;
  patterns: readonly PatternView[];
  review: ReviewStatus;
  trace: readonly TraceRow[];
  memories: readonly MemoryView[];
}> {
  const [sessions, episodes, receipts, patterns, candidates, memories] = await Promise.all([
    learning.listSessions(),
    learning.listEpisodes(),
    learning.listReceipts(),
    learning.listPatterns(),
    learning.listCandidates(),
    learning.listMemories(),
  ]);
  const open = sessions.find((session) => session.termination === "open") ?? null;
  const latestEpisode = [...episodes].reverse()[0] ?? null;
  const completeSessions = sessions.filter(
    (session) => session.termination === "completed" && session.episodeCount > 0,
  ).length;
  const completeEpisodes = episodes.filter((episode) => episode.outcome === "completed").length;
  const approvedReflexes = candidates.filter(
    (candidate) => candidate.state === "approved" || candidate.state === "active",
  ).length;
  const qualifiedCandidates = candidates.filter((candidate) => candidate.state !== "observing").length;
  const trigger = reviewTrigger({
    completeSessions,
    approvedReflexes,
    completeEpisodes,
    qualifiedCandidates,
  });
  const latest = receipts.at(-1) ?? null;
  return {
    runtime: {
      ...header,
      sessionId: open?.sessionId ?? null,
      episodeId: latestEpisode?.episodeId ?? null,
    },
    gate: latest ? toGate(latest) : null,
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
      approvedReflexes,
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
      type: event.type,
      reasonCode: event.reasonCode ?? null,
      latencyMs: event.latencyMs ?? null,
      caseId: event.caseId ?? null,
      result: event.selectedOutcome ?? null,
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
  return {
    at: receipt.createdAt,
    gateId: receipt.gateId,
    policyVersion: receipt.policyVersion,
    reflexId: receipt.gateId.startsWith("reflex.") ? receipt.gateId : null,
    questionType: receipt.questionType,
    optionIds: Object.keys(receipt.probabilities),
    probabilities: receipt.probabilities,
    topProbability: top,
    margin,
    threshold,
    result: receipt.result,
    reasonCode: receipt.reasonCode,
    provider: receipt.provider,
    latencyMs: receipt.latencyMs,
    retries: receipt.retries,
    nextAction: nextAction(receipt),
  };
}

function nextAction(receipt: ReceiptRecord): string {
  if (receipt.result === "wait") return "bounded_wait";
  if (receipt.result === "fail") return "no_definition";
  if (receipt.result === "not_applicable") return "local_result";
  if (receipt.questionType === "user") return "stored";
  return "continue";
}
