import type { JudgmentPort, JudgmentResponse, TextModelPort } from "@relay/contracts";
import { createRelayClient, type EngineDeps } from "@relay/engine";
import { MemoryArtifactStore } from "./memory-artifacts.js";
import { SqliteEngineStore } from "./sqlite-store.js";

export type NodeHarnessOptions = {
  readonly sessionId?: string;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
};

export function createNodeHarness(options: NodeHarnessOptions = {}) {
  const store = new SqliteEngineStore();
  const artifacts = new MemoryArtifactStore();
  let n = 0;
  const clock = options.clock ?? { now: () => new Date() };
  const ids =
    options.ids ??
    ({
      next: (prefix: string) => `${prefix}_${++n}`,
    } satisfies EngineDeps["ids"]);

  const judgments: JudgmentPort = {
    async judge(): Promise<JudgmentResponse> {
      return {
        ok: false,
        failure: { category: "disabled", message: "recorded_judgments_default" },
      };
    },
  };

  const model: TextModelPort = {
    async generate() {
      return { ok: false, failureReason: "model_disabled" };
    },
  };

  const client = createRelayClient({
    store,
    artifacts,
    judgments,
    model,
    clock,
    ids,
    sessionId: options.sessionId ?? "session_test",
  });

  return { client, store, artifacts, close: () => store.close() };
}

export { MemoryArtifactStore, SqliteEngineStore };
