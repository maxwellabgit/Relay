import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import { AppState } from "react-native";
import {
  SESSION_JEV_GRANT_DEFAULTS,
  unselectedModelDelivery,
  type ActionCard,
  type ModelDeliveryView,
  type RelayClient,
  type RelayCommand,
  type RelaySnapshot,
} from "@relay/contracts";
import { ModelDelivery, buildRedactedDiagnostics } from "@relay/engine";
import { isRetrying, productSurface, RelayWorkbench, type ThreadScroll } from "@relay/ui";
import { createAppClient, type AppClientHandle } from "./bootstrap/createAppClient";
import { developerConsoleAllowed, readProcessEnv } from "./bootstrap/dev-console";
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
  const [e2eFixture, setE2eFixture] = useState(false);
  const [phase, setPhase] = useState<"booting" | "failed" | "live">("booting");
  const [composerText, setComposerText] = useState("");
  const [composerSending, setComposerSending] = useState(false);
  const threadScroll = useRef<ThreadScroll>({ pinned: true, offset: 0 });
  const modelDeliveryRef = useRef(new ModelDelivery(null));
  const modelAbort = useRef<AbortController | null>(null);
  const [modelDelivery, setModelDelivery] = useState<ModelDeliveryView>(() => unselectedModelDelivery());

  const beginModelDownload = () => {
    const controller = new AbortController();
    modelAbort.current = controller;
    void modelDeliveryRef.current.start(controller.signal).then(setModelDelivery);
  };

  const runCommand = async (command: RelayCommand): Promise<boolean> => {
    const client = clientRef.current;
    if (!client) {
      setNotice("client_not_ready");
      return false;
    }
    inFlight.current += 1;
    setBusy(true);
    setNotice(null);
    try {
      const result = await client.execute(command);
      if (!result.ok) {
        setNotice(result.error ?? result.summary);
        return false;
      }
      return true;
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "command_failed");
      return false;
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
        setE2eFixture(created.e2eFixture === true);
        unsubscribe = created.client.subscribe((change) => {
          if (change.type === "SnapshotReplaced") {
            setSnapshot(change.snapshot);
            setPhase("live");
          }
        });
        void created.start().catch((error: unknown) => {
          if (!cancelled) {
            setPhase("failed");
            setNotice(error instanceof Error ? error.message : "start_failed");
          }
        });
        void created.secrets.status().then((status) => {
          if (!cancelled) setTypeSafeKeyStatus(status);
        });
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setPhase("failed");
          setNotice(error instanceof Error ? error.message : "client_failed");
        }
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

  const engineChip = snapshot.status.find((chip) => chip.id === "engine");
  const surface = productSurface({
    phase,
    busy,
    queueDepth: snapshot.queueDepth,
    waitCount: snapshot.waits.length,
    engineOk: engineChip ? engineChip.ok : null,
    retrying: isRetrying(snapshot.trace),
  });
  const devConsole = developerConsoleAllowed(readProcessEnv());

  return (
    <ProductErrorBoundary>
      <RelayWorkbench
        snapshot={snapshot}
        typeSafeKeyStatus={typeSafeKeyStatus}
        showDeveloperPanel={devConsole}
        busy={busy}
        notice={notice}
        surface={surface}
        onListenChange={(enabled) => {
          void runCommand({ type: "SetListening", enabled });
        }}
        onSubmit={(text) => runCommand({ type: "SubmitText", text })}
        composerText={composerText}
        onComposerText={setComposerText}
        composerSending={composerSending}
        onComposerSending={setComposerSending}
        threadScroll={threadScroll}
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
        onGrantJevDisclosure={() => {
          void runCommand({ type: "GrantJevDisclosure", ...SESSION_JEV_GRANT_DEFAULTS });
        }}
        onRevokeJevDisclosure={(grantId) => {
          void runCommand({ type: "RevokeJevDisclosure", grantId });
        }}
        onRefreshHealth={() => {
          void runCommand({ type: "RefreshProviderHealth" }).then(async () => {
            const status = await handleRef.current?.secrets.status();
            if (status) setTypeSafeKeyStatus(status);
          });
        }}
        onImportTypeSafeKey={async () => {
          const secrets = handleRef.current?.secrets;
          if (!secrets?.import) {
            setNotice("secrets_unavailable");
            return;
          }
          try {
            await secrets.import();
            setTypeSafeKeyStatus(await secrets.status());
            await clientRef.current?.execute({ type: "RefreshProviderHealth" });
          } catch (error) {
            const message = error instanceof Error ? error.message : "secret_import_failed";
            if (message !== "secret_picker_cancelled") setNotice(message);
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
        onDecideVerify={(verifyId, decision, correction) => {
          void runCommand({
            type: "DecideVerify",
            verifyId,
            decision,
            ...(correction ? { correction } : {}),
          });
        }}
        onRenameCase={(projectCaseId, alias, expectedVersion) => {
          void runCommand({ type: "RenameProjectCase", projectCaseId, alias, expectedVersion });
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
        onExportDiagnostics={() => JSON.stringify(buildRedactedDiagnostics(snapshot), null, 2)}
        modelDelivery={modelDelivery}
        onModelDownload={beginModelDownload}
        onModelResume={beginModelDownload}
        onModelPause={() => {
          modelAbort.current?.abort();
          setModelDelivery(modelDeliveryRef.current.pause());
        }}
        onModelCancel={() => {
          modelAbort.current?.abort();
          setModelDelivery(modelDeliveryRef.current.cancel());
        }}
        onModelDelete={() => {
          setModelDelivery(modelDeliveryRef.current.deleteLocal());
        }}
        {...(isTauriHost() ? { onOpenLog: () => { void openRunFolder(); } } : {})}
        e2eFixture={e2eFixture}
        onE2eCalendar={() => {
          void handleRef.current?.engine.installCalendarFixture().catch((error: unknown) => {
            setNotice(error instanceof Error ? error.message : "sample_failed");
          });
        }}
        onInjectSample={() => {
          void (async () => {
            const handle = handleRef.current;
            if (!developerConsoleAllowed(readProcessEnv()) || !handle) return;
            const { injectBirthdaySample } = await import("./dev/calendar-sample.js");
            await injectBirthdaySample(handle.engine);
          })().catch((error: unknown) => {
            setNotice(error instanceof Error ? error.message : "sample_failed");
          });
        }}
        onReplayFixture={async (fixture, speed) => {
          const handle = handleRef.current;
          if (!developerConsoleAllowed(readProcessEnv()) || !handle || fixture !== "acronym-basic") return;
          try {
            const { replayAcronymFixture } = await import("./dev/replay-acronym-fixture.js");
            await replayAcronymFixture(handle.engine, snapshot.runtime.sessionId ?? "session_web", speed);
          } catch (error) {
            setNotice(error instanceof Error ? error.message : "replay_failed");
          }
        }}
      />
      <StatusBar style="light" />
    </ProductErrorBoundary>
  );
}

function isTauriHost(): boolean {
  return Boolean(
    (globalThis as { __TAURI_INTERNALS__?: { invoke?: unknown } }).__TAURI_INTERNALS__?.invoke,
  );
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

