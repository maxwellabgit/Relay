import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import { AppState } from "react-native";
import type { ActionCard, RelayClient, RelayCommand, RelaySnapshot } from "@relay/contracts";
import { ACRONYM_BASIC_EVENTS } from "@relay/testkit/browser";
import { RelayWorkbench } from "@relay/ui";
import { createAppClient, type AppClientHandle } from "./bootstrap/createAppClient";
import { ProductErrorBoundary } from "./ProductErrorBoundary";

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
  const inFlight = useRef(0);
  const [snapshot, setSnapshot] = useState<RelaySnapshot>(EMPTY_SNAPSHOT);
  const [typeSafeKeyStatus, setTypeSafeKeyStatus] = useState<"present" | "disabled" | "unknown">(
    "unknown",
  );
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const runCommand = async (command: RelayCommand): Promise<void> => {
    const client = clientRef.current;
    if (!client) {
      setNotice("client_not_ready");
      return;
    }
    inFlight.current += 1;
    setBusy(true);
    setNotice(null);
    try {
      const result = await client.execute(command);
      if (!result.ok) setNotice(result.error ?? result.summary);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "command_failed");
    } finally {
      inFlight.current = Math.max(0, inFlight.current - 1);
      setBusy(inFlight.current > 0);
    }
  };

  useEffect(() => {
    let cancelled = false;
    let handle: AppClientHandle | null = null;
    let unsubscribe = () => {};

    void createAppClient()
      .then((created) => {
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
        void created.start().catch((error: unknown) => {
          if (!cancelled) setNotice(error instanceof Error ? error.message : "start_failed");
        });
        void created.secrets.status().then((status) => {
          if (!cancelled) setTypeSafeKeyStatus(status);
        });
      })
      .catch((error: unknown) => {
        if (!cancelled) setNotice(error instanceof Error ? error.message : "client_failed");
      });

    const appState = AppState.addEventListener("change", (next) => {
      if (next === "active") return;
      void handleRef.current?.onHostBackground();
    });

    return () => {
      cancelled = true;
      appState.remove();
      unsubscribe();
      clientRef.current = null;
      handleRef.current = null;
      if (handle) void handle.stop();
    };
  }, []);

  return (
    <ProductErrorBoundary>
      <RelayWorkbench
        snapshot={snapshot}
        typeSafeKeyStatus={typeSafeKeyStatus}
        showDeveloperPanel={isDevConsoleEnabled()}
        busy={busy}
        notice={notice}
        onListenChange={(enabled) => {
          void runCommand({ type: "SetListening", enabled });
        }}
        onSubmit={(text) => {
          void runCommand({ type: "SubmitText", text });
        }}
        onCancelActive={() => {
          void runCommand({ type: "CancelActive" });
        }}
        onAction={(action) => {
          void handleAction(clientRef.current, action).catch((error: unknown) => {
            setNotice(error instanceof Error ? error.message : "action_failed");
          });
        }}
        onSetHostedProcessing={(enabled) => {
          void runCommand({ type: "SetHostedProcessing", enabled });
        }}
        onRefreshHealth={() => {
          void runCommand({ type: "RefreshProviderHealth" }).then(async () => {
            const status = await handleRef.current?.secrets.status();
            if (status) setTypeSafeKeyStatus(status);
          });
        }}
        onSetTypeSafeKey={async (value) => {
          const secrets = handleRef.current?.secrets;
          if (!secrets) {
            setNotice("secrets_unavailable");
            return;
          }
          try {
            await secrets.set(value);
            setTypeSafeKeyStatus(await secrets.status());
            await clientRef.current?.execute({ type: "RefreshProviderHealth" });
          } catch (error) {
            setNotice(error instanceof Error ? error.message : "secret_set_failed");
          }
        }}
        onDeleteTypeSafeKey={async () => {
          const secrets = handleRef.current?.secrets;
          if (!secrets) {
            setNotice("secrets_unavailable");
            return;
          }
          try {
            await secrets.delete();
            setTypeSafeKeyStatus(await secrets.status());
            await clientRef.current?.execute({ type: "RefreshProviderHealth" });
          } catch (error) {
            setNotice(error instanceof Error ? error.message : "secret_delete_failed");
          }
        }}
        onStartSession={() => {
          void runCommand({ type: "StartWorkSession" });
        }}
        onEndSession={() => {
          void runCommand({ type: "EndWorkSession" });
        }}
        onApproveCandidate={(candidateId) => {
          void runCommand({ type: "ApproveCandidate", candidateId });
        }}
        onAcceptAmbient={(recommendationId) => {
          void runCommand({ type: "AcceptAmbientRecommendation", recommendationId });
        }}
        onDismissAmbient={(recommendationId) => {
          void runCommand({ type: "DismissAmbientRecommendation", recommendationId });
        }}
        onFeedbackAmbient={(recommendationId, feedback) => {
          void runCommand({
            type: "FeedbackAmbientRecommendation",
            recommendationId,
            feedback,
          });
        }}
        onActivateReflex={(reflexId, version, stateVersion) => {
          void runCommand({
            type: "ActivateReflex",
            reflex: { id: reflexId, version },
            expectedStateVersion: stateVersion,
          });
        }}
        onPauseReflex={(reflexId, version, stateVersion) => {
          void runCommand({
            type: "PauseReflex",
            reflex: { id: reflexId, version },
            expectedStateVersion: stateVersion,
          });
        }}
        onRollbackReflex={(reflexId, version, stateVersion) => {
          void runCommand({
            type: "RollbackReflex",
            reflex: { id: reflexId, version },
            expectedStateVersion: stateVersion,
          });
        }}
        onRejectCandidate={(candidateId) => {
          void runCommand({ type: "RejectCandidate", candidateId });
        }}
        onSnoozeCandidate={(candidateId) => {
          void runCommand({ type: "SnoozeCandidate", candidateId });
        }}
        onOpenLog={() => {
          void openRunFolder();
        }}
        onReplayFixture={async (fixture, speed) => {
          const handle = handleRef.current;
          if (!handle || fixture !== "acronym-basic") return;
          const captureId = `capture_${Date.now()}`;
          let previousAt = 0;
          let index = 0;
          try {
            for (const event of ACRONYM_BASIC_EVENTS) {
              if (event.type !== "segment.final") continue;
              index += 1;
              const gap = speed === 0 ? 0 : Math.max(0, event.atMs - previousAt) / speed;
              previousAt = event.atMs;
              if (gap > 0) await new Promise((resolve) => setTimeout(resolve, gap));
              await handle.engine.ingestReplayFinalSegment({
                ...event.segment,
                segmentId: `${captureId}_${event.segment.segmentId}_${index}`,
                sessionId: snapshot.runtime.sessionId ?? "session_web",
              });
            }
          } catch (error) {
            setNotice(error instanceof Error ? error.message : "replay_failed");
          }
        }}
      />
      <StatusBar style="light" />
    </ProductErrorBoundary>
  );
}

function isDevConsoleEnabled(): boolean {
  const value = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env
    ?.EXPO_PUBLIC_RELAY_DEV_CONSOLE;
  return value === "1" || value === "true";
}

async function handleAction(client: RelayClient | null, action: ActionCard): Promise<void> {
  if (!client) return;
  if (action.kind === "save_definition" || action.kind === "replace_memory") {
    if (!action.token || !action.expansion) return;
    const saved = await client.execute({
      type: "UpsertGlossaryEntry",
      token: action.token,
      expansion: action.expansion,
      confirmed: true,
      ...(action.kind === "replace_memory" ? { replace: true } : {}),
    });
    if (!saved.ok) throw new Error(saved.error ?? saved.summary);
    return;
  }
  if (action.kind === "confirm_birthday") {
    if (!action.displayName || !action.date) return;
    const saved = await client.execute({
      type: "CaptureBirthday",
      displayName: action.displayName,
      date: action.date,
      confirmed: true,
    });
    if (!saved.ok) throw new Error(saved.error ?? saved.summary);
  }
}

async function openRunFolder(): Promise<void> {
  const host = globalThis as { __TAURI_INTERNALS__?: { invoke?: (command: string) => Promise<unknown> } };
  if (host.__TAURI_INTERNALS__?.invoke) {
    await host.__TAURI_INTERNALS__.invoke("open_run_folder");
  }
}

