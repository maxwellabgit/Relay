import type { JudgmentPort, JudgmentRequest, JudgmentResponse, RelayClient, TextModelPort } from "@relay/contracts";
import { TauriEngineStore, type StoreInvoke } from "@relay/adapter-tauri/engine-store";
import {
  createProductionIds,
  createRelayClientFromEngine,
  parseTypeSafeBody,
  RelayEngine,
  TYPESAFE_MODEL,
  type EngineDeps,
} from "@relay/engine";
import { productionReflexes } from "@relay/reflexes";
import { MemoryArtifactStore } from "@relay/testkit/browser";
import { createBrowserTraceSink } from "./trace-log";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

type TauriHost = {
  __TAURI_INTERNALS__?: { invoke?: TauriInvoke };
};

export type DesktopClientOptions = {
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
  readonly invoke?: TauriInvoke;
};

export type DesktopClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly store: TauriEngineStore;
  readonly artifacts: MemoryArtifactStore;
  start(): Promise<void>;
  stop(): Promise<void>;
};

export async function createDesktopClient(options: DesktopClientOptions = {}): Promise<DesktopClientHandle> {
  const invoke = options.invoke ?? requireTauriInvoke();
  const secret = String((await invoke("secret_status")) ?? "disabled");
  const runId = `run_${Date.now().toString(36)}`;
  const directory = await invoke("trace_run_dir", { runId }).catch(() => null);
  const directoryLabel =
    typeof directory === "string" && directory.length > 0
      ? directory
      : `%LOCALAPPDATA%\\RELAY\\runs\\${runId}`;
  const ids = options.ids ?? createProductionIds();
  const storeInvoke: StoreInvoke = (command, args) => invoke(command, args);
  const store = new TauriEngineStore(storeInvoke);
  const artifacts = new MemoryArtifactStore();
  const judgments: JudgmentPort = options.judgments ?? createNativeJudgmentPort(invoke);
  const model: TextModelPort = {
    async generate() {
      return { ok: false, failureReason: "model_disabled" };
    },
  };

  const deps: EngineDeps = {
    store,
    artifacts,
    judgments,
    model,
    clock: options.clock ?? { now: () => new Date() },
    ids,
    sessionId: ids.next("session"),
    reflexModules: productionReflexes,
    storageDetail: "sqlite",
    jevStatus:
      secret === "present"
        ? { ok: true, detail: "native typesafe" }
        : { ok: false, detail: "missing key" },
    modelStatus: { ok: false, detail: "disabled" },
    mode: "live",
    gitCommit: "unknown",
    trace: createBrowserTraceSink(runId, directoryLabel),
  };

  const engine = new RelayEngine(deps);
  const client = createRelayClientFromEngine(engine);
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

function requireTauriInvoke(): TauriInvoke {
  const invoke = (globalThis as TauriHost).__TAURI_INTERNALS__?.invoke;
  if (!invoke) throw new Error("tauri_invoke_missing");
  return invoke;
}
