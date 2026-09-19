import type { RelaySnapshot, StatusChipState } from "@relay/contracts";
import type { EngineStore } from "./store.js";

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
    gate: null,
    expansion: {
      completeSessions: 0,
      sessionTarget: 12,
      reflexesBuilt: 0,
      reflexTarget: 4,
      reviewDue: false,
    },
    recommendations: [],
    decisions: [],
    decisionLogPath: ".dev-data/dev-console/decisions.jsonl",
  };
}
