import type {
  ArtifactStorePort,
  JudgmentPort,
  JudgmentRequest,
  JudgmentResponse,
  RelayClient,
  RelayCommand,
  RelayCommandResult,
  TextModelPort,
} from "@relay/contracts";
import { TauriArtifactStore } from "@relay/adapter-tauri/artifact-store";
import { startLiveTranscriptPump, TauriAudioPort, type AudioStatus } from "@relay/adapter-tauri/audio";
import { TauriEngineStore, type StoreInvoke } from "@relay/adapter-tauri/engine-store";
import { TauriLocalModelPort } from "@relay/adapter-tauri/local-model";
import {
  createProductionIds,
  createRelayClientFromEngine,
  JevHealthTracker,
  parseTypeSafeBody,
  RelayEngine,
  TYPESAFE_MODEL,
  type EngineDeps,
} from "@relay/engine";
import { createProductionReflexes } from "@relay/reflexes";
import { createBrowserTraceSink } from "./trace-log";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

type TauriHost = {
  __TAURI_INTERNALS__?: { invoke?: TauriInvoke };
};

export type DesktopClientOptions = {
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
  readonly model?: TextModelPort;
  readonly invoke?: TauriInvoke;
};

export type DesktopClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly store: TauriEngineStore;
  readonly artifacts: ArtifactStorePort;
  start(): Promise<void>;
  stop(): Promise<void>;
};

type MutableHealth = { ok: boolean; detail: string; model?: string | null };

const HEALTH_POLL_MS = 60_000;

export async function createDesktopClient(options: DesktopClientOptions = {}): Promise<DesktopClientHandle> {
  const invoke = options.invoke ?? requireTauriInvoke();
  const localModel = options.model ?? new TauriLocalModelPort(invoke);
  const audio = new TauriAudioPort(invoke);

  const jevTracker = new JevHealthTracker();
  const jevHealth = jevTracker.state;
  const modelHealth: MutableHealth = { ok: false, detail: "external:unavailable", model: null };
  const audioHealth: MutableHealth = { ok: false, detail: "not connected" };

  const runId = `run_${Date.now().toString(36)}`;
  const directory = await invoke("trace_run_dir", { runId }).catch(() => null);
  const directoryLabel =
    typeof directory === "string" && directory.length > 0
      ? directory
      : `%LOCALAPPDATA%\\RELAY\\diagnostics\\runs\\${runId}`;
  const ids = options.ids ?? createProductionIds();
  const sessionId = ids.next("session");
  const storeInvoke: StoreInvoke = (command, args) => invoke(command, args);
  const artifacts = new TauriArtifactStore(invoke);
  const store = new TauriEngineStore(storeInvoke, artifacts);
  const nativeJudgments: JudgmentPort = options.judgments ?? createNativeJudgmentPort(invoke);
  let refreshConfiguredHealth: () => Promise<void> = async () => {};

  const judgments: JudgmentPort = {
    async judge(request, signal) {
      const response = await nativeJudgments.judge(request, signal);
      // Only JudgmentPort outcomes may create Jev provider evidence.
      if (response.ok) {
        jevTracker.noteSuccess();
      } else {
        jevTracker.noteFailure();
      }
      void refreshConfiguredHealth();
      return response;
    },
  };

  const deps: EngineDeps = {
    store,
    artifacts,
    judgments,
    model: localModel,
    clock: options.clock ?? { now: () => new Date() },
    ids,
    sessionId,
    reflexModules: createProductionReflexes(store.learning),
    storageDetail: "sqlite",
    jevStatus: jevHealth,
    modelStatus: modelHealth,
    audioStatus: audioHealth,
    mode: "live",
    gitCommit: resolveBuildSha(),
    trace: createBrowserTraceSink(runId, directoryLabel),
  };

  const engine = new RelayEngine(deps);
  const inner = createRelayClientFromEngine(engine);
  let stopPump: (() => void) | null = null;
  let acceptIntake = true;
  let handlingSourceFailure = false;
  let healthTimer: ReturnType<typeof setInterval> | null = null;

  const applyAudioHealth = (status: AudioStatus): void => {
    audioHealth.ok = status.ok;
    audioHealth.detail = status.capturing ? "capturing" : status.detail;
  };

  /**
   * Configuration + local capability refresh only.
   * Does not call noteSuccess/noteFailure — those require a real JudgmentPort result.
   */
  const refreshConfiguredHealthImpl = async (): Promise<void> => {
    const secret = String((await invoke("secret_status").catch(() => "disabled")) ?? "disabled");
    const hosted = await store.getHostedProcessingEnabled().catch(() => false);

    jevTracker.apply({
      secretPresent: secret === "present",
      hostedEnabled: hosted,
    });

    try {
      if ("status" in localModel && typeof (localModel as TauriLocalModelPort).status === "function") {
        const status = await (localModel as TauriLocalModelPort).status();
        modelHealth.ok = status.ok;
        modelHealth.model = status.model ?? null;
        modelHealth.detail = status.ok
          ? status.model
            ? `external:ready · ${status.model}`
            : "external:ready"
          : status.detail?.startsWith("external:")
            ? status.detail
            : `external:${status.detail || "unavailable"}`;
      } else {
        modelHealth.ok = false;
        modelHealth.detail = "external:unavailable";
        modelHealth.model = null;
      }
    } catch {
      modelHealth.ok = false;
      modelHealth.detail = "external:unavailable";
      modelHealth.model = null;
    }

    try {
      const status = await audio.status();
      applyAudioHealth(status);
    } catch {
      audioHealth.ok = false;
      audioHealth.detail = "unavailable";
    }

    await engine.refreshSnapshot().catch(() => undefined);
  };
  refreshConfiguredHealth = refreshConfiguredHealthImpl;

  await refreshConfiguredHealthImpl();

  const client: RelayClient = {
    start: async () => {
      await inner.start();
      stopPump?.();
      acceptIntake = true;
      stopPump = startLiveTranscriptPump({
        audio,
        sessionId,
        ingest: async (segment) => {
          if (!acceptIntake) return;
          await engine.ingestFinalSegment(segment, false);
        },
        onStatus: async (status) => {
          const wasCapturing = audioHealth.detail === "capturing";
          applyAudioHealth(status);
          const sourceDied =
            wasCapturing && !status.capturing && (!status.ok || status.detail === "source_exited");
          if (sourceDied && !handlingSourceFailure) {
            handlingSourceFailure = true;
            try {
              acceptIntake = false;
              const listening = await store.getListening(sessionId).catch(() => false);
              if (listening) {
                await inner.execute({ type: "SetListening", enabled: false });
              }
              await refreshConfiguredHealthImpl();
            } catch {
              await engine.refreshSnapshot().catch(() => undefined);
            } finally {
              handlingSourceFailure = false;
            }
            return;
          }
          await engine.refreshSnapshot().catch(() => undefined);
        },
      });
      if (healthTimer) clearInterval(healthTimer);
      healthTimer = setInterval(() => {
        void refreshConfiguredHealthImpl();
      }, HEALTH_POLL_MS);
    },
    stop: async () => {
      acceptIntake = false;
      if (healthTimer) {
        clearInterval(healthTimer);
        healthTimer = null;
      }
      stopPump?.();
      stopPump = null;
      try {
        await audio.stop();
      } catch {
        /* ignore */
      }
      await inner.stop();
      await invoke("complete_trace_run", { runId }).catch(() => null);
    },
    getSnapshot: () => inner.getSnapshot(),
    subscribe: (listener) => inner.subscribe(listener),
    execute: async (command: RelayCommand): Promise<RelayCommandResult> => {
      if (command.type === "RefreshProviderHealth") {
        await refreshConfiguredHealthImpl();
        return { ok: true, summary: "provider_health_refreshed" };
      }

      if (command.type === "SetHostedProcessing") {
        const result = await inner.execute(command);
        await refreshConfiguredHealthImpl();
        return result;
      }

      if (command.type === "SetListening") {
        if (command.enabled) {
          try {
            const status = await audio.start(sessionId);
            applyAudioHealth(status);
            if (!status.ok || !status.capturing) {
              await refreshConfiguredHealthImpl();
              return {
                ok: false,
                summary: "audio_unavailable",
                error: status.detail || "asr_unavailable",
              };
            }
            acceptIntake = true;
            const result = await inner.execute(command);
            await engine.refreshSnapshot();
            return result;
          } catch {
            audioHealth.ok = false;
            audioHealth.detail = "unavailable";
            await refreshConfiguredHealthImpl();
            return { ok: false, summary: "audio_unavailable", error: "unavailable" };
          }
        }

        try {
          // Disable: stop accepting → stop source → drain → commit → Listening=false
          acceptIntake = false;
          const status = await audio.stop();
          const drained = await audio.drain();
          for (const event of drained) {
            if (event.type === "segment.final") {
              await engine.ingestFinalSegment({ ...event.segment, sessionId }, false);
            }
          }
          applyAudioHealth({ ...status, detail: "idle", capturing: false, ok: true });
          const result = await inner.execute(command);
          await engine.refreshSnapshot();
          return result;
        } catch {
          audioHealth.ok = false;
          audioHealth.detail = "unavailable";
          await refreshConfiguredHealthImpl();
          return { ok: false, summary: "audio_stop_failed", error: "unavailable" };
        }
      }

      const result = await inner.execute(command);
      return result;
    },
  };

  return {
    client,
    engine,
    store,
    artifacts,
    start: () => client.start(),
    stop: () => client.stop(),
  };
}

function createNativeJudgmentPort(invoke: TauriInvoke): JudgmentPort {
  return {
    async judge(request: JudgmentRequest, signal: AbortSignal): Promise<JudgmentResponse> {
      if (signal.aborted) {
        return { ok: false, failure: { category: "cancelled", message: "jev_cancelled" } };
      }
      const result = (await invoke("typesafe_judge", {
        request: {
          model: request.model || TYPESAFE_MODEL,
          body: {
            model: request.model || TYPESAFE_MODEL,
            questions: request.questions,
            state: request.state,
            questionSetId: request.questionSetId,
            questionSetVersion: request.questionSetVersion,
          },
        },
      })) as {
        ok: boolean;
        category: string;
        latency_ms?: number;
        body?: unknown;
      };
      if (!result?.ok) {
        const category = (result?.category || "missing_secret") as JudgmentResponse extends {
          ok: false;
          failure: { category: infer C };
        }
          ? C
          : "missing_secret";
        return {
          ok: false,
          failure: { category, message: result?.category || "missing_secret" },
        };
      }
      return parseTypeSafeBody(JSON.stringify(result.body ?? {}), Number(result.latency_ms ?? 0));
    },
  };
}

function resolveBuildSha(): string {
  const env = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;
  const sha = env?.EXPO_PUBLIC_GIT_SHA ?? env?.GIT_COMMIT ?? env?.GITHUB_SHA;
  if (sha && /^[0-9a-f]{7,40}$/i.test(sha)) return sha.toLowerCase();
  return "unknown";
}

function requireTauriInvoke(): TauriInvoke {
  const invoke = (globalThis as TauriHost).__TAURI_INTERNALS__?.invoke;
  if (!invoke) throw new Error("tauri_invoke_missing");
  return invoke;
}
