import type {
  ArtifactStorePort,
  FeedItemRecord,
  FeedItemSnapshot,
  RelaySnapshot,
  StatusChipState,
} from "@relay/contracts";
import { HostedGrantLedger, sessionDisclosureView } from "./disclosure/hosted-grant.js";
import type { EngineStore } from "./store.js";
import { RETENTION_LABEL } from "./learning-store.js";

export async function hydrateFeedItems(
  records: readonly FeedItemRecord[],
  artifacts: ArtifactStorePort,
): Promise<readonly FeedItemSnapshot[]> {
  const items: FeedItemSnapshot[] = [];
  for (const record of records) {
    try {
      const bytes = await artifacts.get({
        artifactId: record.contentArtifactId,
        sha256: record.contentSha256,
        policy: { disclosure: "local_only", sensitivity: 0 },
      });
      const summary = new TextDecoder().decode(bytes);
      items.push({
        itemId: record.itemId,
        kind: record.kind,
        summary,
        createdAt: record.createdAt,
        ...(record.caseId ? { caseId: record.caseId } : {}),
      });
    } catch {
      items.push({
        itemId: record.itemId,
        kind: record.kind,
        summary: "[unavailable]",
        createdAt: record.createdAt,
        ...(record.caseId ? { caseId: record.caseId } : {}),
      });
    }
  }
  return items;
}

export async function projectSnapshot(
  store: EngineStore,
  sessionId: string,
  status: readonly StatusChipState[],
  artifacts: ArtifactStorePort,
  now = new Date().toISOString(),
): Promise<RelaySnapshot> {
  const [listening, hostedProcessingEnabled, cases, records, sourceSegments, queueDepth, events] =
    await Promise.all([
      store.getListening(sessionId),
      store.getHostedProcessingEnabled(),
      store.listActiveCases(),
      store.listFeedItemRecords(),
      store.listSourceSegments(sessionId),
      store.countWorkItems(),
      store.listDomainEvents(80),
    ]);
  const feedItems = await hydrateFeedItems(records, artifacts);
  const disclosure = sessionDisclosureView(
    await new HostedGrantLedger(store).read({ kind: "session", id: sessionId }),
    now,
  );

  return {
    listening,
    hostedProcessingEnabled,
    jevDisclosure: disclosure,
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
    decision: null,
    caseExecution: null,
    currentInputPreview: null,
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
