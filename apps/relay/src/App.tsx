import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import type { ActionCard, RelayClient, RelaySnapshot } from "@relay/contracts";
import { RelayWorkbench } from "@relay/ui";
import { createAppClient, type AppClientHandle } from "./bootstrap/createAppClient";
import { ACRONYM_BASIC_EVENTS } from "./fixtures/acronym-basic";

const EMPTY_SNAPSHOT: RelaySnapshot = {
  listening: false,
  feedItems: [],
  approvals: [],
  connections: [],
  reflexes: [],
  providerHealth: [],
  waits: [],
  status: [],
  sourceSegments: [],
  cases: [],
  queueDepth: 0,
  activity: [],
  runtime: {
    runId: "",
    commit: "unknown",
    sessionId: null,
    episodeId: null,
    queueDepth: 0,
    logPath: "",
    logWritable: false,
    logError: null,
    mode: "live",
    retention: "trace 30d/100MB · failed 7d · memories until delete",
    deadLetters: 0,
    storageAdapter: "ephemeral demo",
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

export function App() {
  const handleRef = useRef<AppClientHandle | null>(null);
  const clientRef = useRef<RelayClient | null>(null);
  const [snapshot, setSnapshot] = useState<RelaySnapshot>(EMPTY_SNAPSHOT);

  useEffect(() => {
    let cancelled = false;
    let handle: AppClientHandle | null = null;
    let unsubscribe = () => {};

    void createAppClient().then((created) => {
      if (cancelled) {
        void created.stop();
        return;
      }
      handle = created;
      handleRef.current = created;
      clientRef.current = created.client;
      unsubscribe = created.client.subscribe((change) => {
        if (change.type === "SnapshotReplaced") setSnapshot(change.snapshot);
      });
      void created.start();
    });

    return () => {
      cancelled = true;
      unsubscribe();
      clientRef.current = null;
      handleRef.current = null;
      if (handle) void handle.stop();
    };
  }, []);

  return (
    <>
      <RelayWorkbench
        snapshot={snapshot}
        onListenChange={(enabled) => {
          void clientRef.current?.execute({ type: "SetListening", enabled });
        }}
        onSubmit={(text) => {
          void clientRef.current?.execute({ type: "SubmitText", text });
        }}
        onAction={(action) => {
          void handleAction(clientRef.current, action);
        }}
        onStartSession={() => {
          void clientRef.current?.execute({ type: "StartWorkSession" });
        }}
        onEndSession={() => {
          void clientRef.current?.execute({ type: "EndWorkSession" });
        }}
        onApproveCandidate={(candidateId) => {
          void clientRef.current?.execute({ type: "ApproveCandidate", candidateId });
        }}
        onRejectCandidate={(candidateId) => {
          void clientRef.current?.execute({ type: "RejectCandidate", candidateId });
        }}
        onSnoozeCandidate={(candidateId) => {
          void clientRef.current?.execute({ type: "SnoozeCandidate", candidateId });
        }}
        onOpenLog={() => {
          void openRunFolder();
        }}
        onReplayFixture={async (fixture, speed) => {
          const handle = handleRef.current;
          if (!handle || fixture !== "acronym-basic") return;
          await clientRef.current?.execute({ type: "SetListening", enabled: true });
          let previousAt = 0;
          for (const event of ACRONYM_BASIC_EVENTS) {
            if (event.type !== "segment.final") continue;
            const gap = speed === 0 ? 0 : Math.max(0, event.atMs - previousAt) / speed;
            previousAt = event.atMs;
            if (gap > 0) await new Promise((resolve) => setTimeout(resolve, gap));
            await handle.engine.ingestFinalSegment({ ...event.segment, sessionId: snapshot.runtime.sessionId ?? "session_web" }, false);
          }
        }}
      />
      <StatusBar style="light" />
    </>
  );
}

async function handleAction(client: RelayClient | null, action: ActionCard): Promise<void> {
  if (!client) return;
  if (action.kind === "save_definition" || action.kind === "replace_memory") {
    if (!action.token || !action.expansion) return;
    await client.execute({
      type: "UpsertGlossaryEntry",
      token: action.token,
      expansion: action.expansion,
      confirmed: true,
      ...(action.kind === "replace_memory" ? { replace: true } : {}),
    });
    return;
  }
  if (action.kind === "confirm_birthday") {
    if (!action.displayName || !action.date) return;
    await client.execute({
      type: "CaptureBirthday",
      displayName: action.displayName,
      date: action.date,
      confirmed: true,
    });
  }
}

async function openRunFolder(): Promise<void> {
  const host = globalThis as { __TAURI_INTERNALS__?: { invoke?: (command: string) => Promise<unknown> } };
  if (host.__TAURI_INTERNALS__?.invoke) {
    await host.__TAURI_INTERNALS__.invoke("open_run_folder");
  }
}
