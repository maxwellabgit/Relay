import type { RelaySnapshot, StatusChipState } from "@relay/contracts";
import type { EngineStore } from "./store.js";
import { RETENTION_LABEL } from "./learning-store.js";

export async function projectSnapshot(
  store: EngineStore,
  sessionId: string,
  status: readonly StatusChipState[],
): Promise<RelaySnapshot> {
  const [listening, cases, feedItems, sourceSegments, queueDepth, events] = await Promise.all([
    store.getListening(sessionId),
    store.listActiveCases(),
    store.listFeedItems(),
    store.listSourceSegments(sessionId),
    store.countWorkItems(),
    store.listDomainEvents(80),
  ]);

  return {
    listening,
    feedItems,
    approvals: [],
    connections: [],
    reflexes: [],
    providerHealth: [],
    waits: cases
      .filter((c) => c.status === "waiting")
      .map((c) => ({
        caseId: c.caseId,
        waitKind: c.waitKind ?? "unknown",
      })),
    status,
    sourceSegments,
    cases: cases.map((c) => ({
      caseId: c.caseId,
      phase: c.phase,
      status: c.status,
      origin: c.origin,
      version: c.version,
    })),
    queueDepth,
    activity: events.flatMap((event) => {
      const message = event.payload.message;
      if (typeof message !== "string" || !message.trim()) return [];
      return [
        {
          sequence: event.sequence,
          at: event.at,
          eventType: event.type,
          message,
        },
      ];
    }),
    runtime: {
      runId: "pending",
      commit: "unknown",
      sessionId: null,
      episodeId: null,
      queueDepth,
      logPath: "",
      logWritable: false,
      logError: "trace_sink_missing",
      mode: "live",
      retention: RETENTION_LABEL,
      deadLetters: 0,
      storageAdapter: "unconfigured",
      activeCaseId: null,
    },
    gate: null,
    patterns: [],
    review: {
      completeSessions: 0,
      sessionTrigger: 12,
      approvedCandidates: 0,
      builtReflexes: 0,
      activeReflexes: 0,
      reflexTrigger: 4,
      completeEpisodes: 0,
      episodeTrigger: 25,
      qualifiedCandidates: 0,
      candidateTrigger: 3,
      reviewDue: false,
      trigger: null,
    },
    trace: [],
    memories: [],
    actions: [],
  };
}
