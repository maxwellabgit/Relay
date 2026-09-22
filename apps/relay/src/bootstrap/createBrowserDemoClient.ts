import type { JudgmentPort, RelayClient, TextModelPort } from "@relay/contracts";
import { createRelayClientFromEngine, createTypeSafeJudgmentPort, RelayEngine, type EngineDeps } from "@relay/engine";
import { productionReflexes } from "@relay/reflexes";
import { MemoryArtifactStore, MemoryEngineStore } from "@relay/testkit/browser";
import { createBrowserTraceSink } from "./trace-log";

export type BrowserDemoOptions = {
  readonly sessionId?: string;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
};

export type BrowserDemoHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly store: MemoryEngineStore;
  readonly artifacts: MemoryArtifactStore;
  readonly secrets: {
    status(): Promise<"present" | "disabled" | "unknown">;
    set(value: string): Promise<void>;
    delete(): Promise<void>;
  };
  onHostBackground(): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
};

/** In-memory browser demo. This is not local durable storage. */
export function createBrowserDemoClient(options: BrowserDemoOptions = {}): BrowserDemoHandle {
  const store = new MemoryEngineStore();
  const artifacts = new MemoryArtifactStore();
  let n = 0;
  const clock = options.clock ?? { now: () => new Date() };
  const ids =
    options.ids ??
    ({
      next: (prefix: string) => `${prefix}_${++n}`,
    } satisfies EngineDeps["ids"]);

  const demoKey = { value: null as string | null };
  const judgments: JudgmentPort =
    options.judgments ??
    createTypeSafeJudgmentPort({
      getApiKey: () => demoKey.value,
      retryDelayMs: 0,
    });

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
    clock,
    ids,
    sessionId: options.sessionId ?? "session_web",
    reflexModules: productionReflexes,
    storageDetail: "ephemeral demo",
    jevStatus: { ok: false, detail: "missing key" },
    modelStatus: { ok: false, detail: "disabled" },
    mode: "live",
    trace: createBrowserTraceSink(),
  };

  const engine = new RelayEngine(deps);
  const client = createRelayClientFromEngine(engine);

  return {
    client,
    engine,
    store,
    artifacts,
    secrets: {
      async status() {
        return demoKey.value ? "present" : "disabled";
      },
      async set(value: string) {
        demoKey.value = value;
      },
      async delete() {
        demoKey.value = null;
      },
    },
    async onHostBackground() {},
    start: () => client.start(),
    stop: () => client.stop(),
  };
}
