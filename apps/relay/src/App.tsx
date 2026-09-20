import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import type { ActionCard, RelayClient, RelaySnapshot } from "@relay/contracts";
import { ACRONYM_BASIC_EVENTS } from "@relay/testkit/browser";
import { RelayWorkbench } from "@relay/ui";
import { createAppClient, type AppClientHandle } from "./bootstrap/createAppClient";

const EMPTY_SNAPSHOT: RelaySnapshot = {
  listening: false,
  hostedProcessingEnabled: false,
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

export function App() {
  const handleRef = useRef<AppClientHandle | null>(null);
  const clientRef = useRef<RelayClient | null>(null);
  const [snapshot, setSnapshot] = useState<RelaySnapshot>(EMPTY_SNAPSHOT);
  const [typeSafeKeyStatus, setTypeSafeKeyStatus] = useState<"present" | "disabled" | "unknown">(
    "unknown",
  );

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
      void refreshTypeSafeKeyStatus().then((status) => {
        if (!cancelled) setTypeSafeKeyStatus(status);
      });
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
        typeSafeKeyStatus={typeSafeKeyStatus}
        onListenChange={(enabled) => {
          void clientRef.current?.execute({ type: "SetListening", enabled });
        }}
        onSubmit={(text) => {
          void clientRef.current?.execute({ type: "SubmitText", text });
        }}
        onAction={(action) => {
          void handleAction(clientRef.current, action);
        }}
        onSetHostedProcessing={(enabled) => {
          void clientRef.current?.execute({ type: "SetHostedProcessing", enabled });
        }}
        onRefreshHealth={() => {
          void clientRef.current?.execute({ type: "RefreshProviderHealth" }).then(async () => {
            setTypeSafeKeyStatus(await refreshTypeSafeKeyStatus());
          });
        }}
        onSetTypeSafeKey={async (value) => {
          await setTypeSafeKey(value);
          setTypeSafeKeyStatus(await refreshTypeSafeKeyStatus());
          await clientRef.current?.execute({ type: "RefreshProviderHealth" });
        }}
        onDeleteTypeSafeKey={async () => {
          await deleteTypeSafeKey();
          setTypeSafeKeyStatus(await refreshTypeSafeKeyStatus());
          await clientRef.current?.execute({ type: "RefreshProviderHealth" });
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
          const captureId = `capture_${Date.now()}`;
          let previousAt = 0;
          let index = 0;
          for (const event of ACRONYM_BASIC_EVENTS) {
            if (event.type !== "segment.final") continue;
            index += 1;
            const gap = speed === 0 ? 0 : Math.max(0, event.atMs - previousAt) / speed;
            previousAt = event.atMs;
            if (gap > 0) await new Promise((resolve) => setTimeout(resolve, gap));
            await handle.engine.ingestFinalSegment(
              {
                ...event.segment,
                segmentId: `${captureId}_${event.segment.segmentId}_${index}`,
                sessionId: snapshot.runtime.sessionId ?? "session_web",
              },
              false,
            );
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

async function refreshTypeSafeKeyStatus(): Promise<"present" | "disabled" | "unknown"> {
  const host = globalThis as {
    __TAURI_INTERNALS__?: { invoke?: (command: string, args?: Record<string, unknown>) => Promise<unknown> };
  };
  if (!host.__TAURI_INTERNALS__?.invoke) return "unknown";
  try {
    const status = String((await host.__TAURI_INTERNALS__.invoke("secret_status")) ?? "disabled");
    return status === "present" ? "present" : "disabled";
  } catch {
    return "unknown";
  }
}

async function setTypeSafeKey(value: string): Promise<void> {
  const host = globalThis as {
    __TAURI_INTERNALS__?: { invoke?: (command: string, args?: Record<string, unknown>) => Promise<unknown> };
  };
  if (!host.__TAURI_INTERNALS__?.invoke) throw new Error("tauri_invoke_missing");
  await host.__TAURI_INTERNALS__.invoke("secret_set", {
    request: { name: "typesafe_api_key", value },
  });
}

async function deleteTypeSafeKey(): Promise<void> {
  const host = globalThis as {
    __TAURI_INTERNALS__?: { invoke?: (command: string, args?: Record<string, unknown>) => Promise<unknown> };
  };
  if (!host.__TAURI_INTERNALS__?.invoke) throw new Error("tauri_invoke_missing");
  await host.__TAURI_INTERNALS__.invoke("secret_delete", {
    request: { name: "typesafe_api_key" },
  });
}
