import type { JudgmentPort, TextModelPort } from "@relay/contracts";
import { createRelayClientFromEngine, RelayEngine, type EngineDeps } from "@relay/engine";
import { productionReflexes } from "@relay/reflexes";
import { RecordedJudgmentPort, recordedSuccess } from "@relay/testkit";
import { MemoryArtifactStore } from "./memory-artifacts.js";
import { SqliteEngineStore } from "./sqlite-store.js";

export type NodeHarnessOptions = {
  readonly sessionId?: string;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
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

  const judgments: JudgmentPort =
    options.judgments ??
    new RecordedJudgmentPort([
      {
        questionSetId: "judgment.acronym-choice",
        response: recordedSuccess({
          expansion: {
            type: "choice",
            choice: "Application Programming Interface",
            probabilities: {
              "Application Programming Interface": 0.82,
              no_match: 0.18,
            },
            confidence: 0.82,
          },
          useful: { type: "noul", probabilityYes: 0.78 },
        }),
      },
    ]);

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
    sessionId: options.sessionId ?? "session_test",
    reflexModules: productionReflexes,
    storageDetail: "sqlite",
    jevDetail: "recorded",
  };

  const engine = new RelayEngine(deps);
  const client = createRelayClientFromEngine(engine);

  return {
    client,
    engine,
    store,
    artifacts,
    close: () => store.close(),
  };
}

export { MemoryArtifactStore, SqliteEngineStore };
