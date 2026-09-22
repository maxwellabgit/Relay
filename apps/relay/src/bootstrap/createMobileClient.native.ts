import type { JudgmentPort, RelayClient, TextModelPort } from "@relay/contracts";
import {
  openMobileBackend,
  type ByteFilePort,
  type MobileBackend,
  type SecretStore,
} from "@relay/adapter-expo";
import { createExpoDocumentFiles, createExpoSecureSecretStore } from "@relay/adapter-expo/durable-host";
import {
  createProductionIds,
  createRelayClientFromEngine,
  createTypeSafeJudgmentPort,
  JevHealthTracker,
  applySpeechSuspend,
  RelayEngine,
  speechOnRelaunch,
  type EngineDeps,
} from "@relay/engine";
import { createProductionReflexes } from "@relay/reflexes";
import { createMobileTraceSink } from "./mobile-trace";

export type MobileClientOptions = {
  readonly backend?: MobileBackend;
  readonly secrets?: SecretStore;
  readonly files?: ByteFilePort;
  readonly judgments?: JudgmentPort;
  readonly model?: TextModelPort;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly sessionId?: string;
};

export type MobileClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly backend: MobileBackend;
  readonly secrets: {
    status(): Promise<"present" | "disabled" | "unknown">;
    set(value: string): Promise<void>;
    delete(): Promise<void>;
  };
  onHostBackground(): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
};

const unavailableModel: TextModelPort = {
  async generate() {
    return { ok: false, failureReason: "model_unavailable" };
  },
};

/**
 * Production Expo composition. Native builds open expo-sqlite.
 * Tests inject a SqlHandle-backed backend. Demo memory stores are not used.
 */
export async function createMobileClient(
  options: MobileClientOptions = {},
): Promise<MobileClientHandle> {
  const secrets = options.secrets ?? createExpoSecureSecretStore();
  const files = options.files ?? createExpoDocumentFiles();
  const backend =
    options.backend ??
    (await openMobileBackend({
      files,
      secrets,
    }));
  const jev = new JevHealthTracker();
  const judgments =
    options.judgments ??
    createTypeSafeJudgmentPort({
      getApiKey: () => secrets.get("typesafe_api_key"),
    });
  const ids = options.ids ?? createProductionIds();
  const sessionId = options.sessionId ?? ids.next("session");
  const runId = `run_${Date.now().toString(36)}`;
  const modelStatus = { ok: false, detail: "mobile_model_pending", model: null as string | null };
  const audioStatus = { ok: false, detail: "speech_unavailable" };

  const deps: EngineDeps = {
    store: backend.store,
    artifacts: backend.artifacts,
    judgments,
    model: options.model ?? unavailableModel,
    clock: options.clock ?? { now: () => new Date() },
    ids,
    sessionId,
    reflexModules: createProductionReflexes(backend.store.learning),
    storageDetail: "sqlite",
    jevStatus: jev.state,
    modelStatus,
    audioStatus,
    mode: "live",
    gitCommit: resolveBuildSha(),
    trace: createMobileTraceSink(files, runId),
  };

  const engine = new RelayEngine(deps);
  const client = createRelayClientFromEngine(engine);

  return {
    client,
    engine,
    backend,
    secrets: {
      async status() {
        const key = await secrets.get("typesafe_api_key");
        return key ? "present" : "disabled";
      },
      async set(value: string) {
        await secrets.set("typesafe_api_key", value);
      },
      async delete() {
        await secrets.delete("typesafe_api_key");
      },
    },
    onHostBackground: async () => {
      const wasListening = await backend.store.getListening(sessionId).catch(() => false);
      const decision = await applySpeechSuspend({
        event: "background",
        wasListening,
        stopCapture: () => backend.lifecycle.background(),
        turnListeningOff: async () => {
          await client.execute({ type: "SetListening", enabled: false });
        },
      });
      audioStatus.detail = decision.detail;
      if (!wasListening) await engine.refreshSnapshot();
    },
    async start() {
      const speech = await backend.speech.status();
      const relaunch = speechOnRelaunch(speech);
      audioStatus.ok = speech.ok;
      audioStatus.detail = relaunch.detail;
      const key = await secrets.get("typesafe_api_key");
      const hosted = await backend.store.getHostedProcessingEnabled().catch(() => false);
      jev.apply({ secretPresent: Boolean(key), hostedEnabled: hosted });
      await client.start();
    },
    async stop() {
      await backend.lifecycle.background();
      await client.stop();
      backend.close();
    },
  };
}

function resolveBuildSha(): string {
  const env = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;
  const sha = env?.EXPO_PUBLIC_GIT_SHA ?? env?.GIT_COMMIT ?? env?.GITHUB_SHA;
  if (sha && /^[0-9a-f]{7,40}$/i.test(sha)) return sha.toLowerCase();
  return "unknown";
}
