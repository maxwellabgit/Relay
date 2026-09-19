import type { RelaySnapshot, StatusChipState } from "@relay/contracts";
import type { EngineStore } from "./store.js";

export async function projectSnapshot(
  store: EngineStore,
  sessionId: string,
  status: readonly StatusChipState[],
): Promise<RelaySnapshot> {
  const [listening, cases, feedItems, sourceSegments, queueDepth] = await Promise.all([
    store.getListening(sessionId),
    store.listActiveCases(),
    store.listFeedItems(),
    store.listSourceSegments(sessionId),
    store.countWorkItems(),
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
  };
}
