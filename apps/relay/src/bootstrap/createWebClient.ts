import type { JudgmentPort, RelayClient, TextModelPort } from "@relay/contracts";
import { createRelayClientFromEngine, RelayEngine, type EngineDeps } from "@relay/engine";
import { productionReflexes } from "@relay/reflexes";
import {
  MemoryArtifactStore,
  MemoryEngineStore,
  RecordedJudgmentPort,
  recordedSuccess,
} from "@relay/testkit/browser";

export type WebClientOptions = {
  readonly sessionId?: string;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
};

export type WebClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly store: MemoryEngineStore;
  readonly artifacts: MemoryArtifactStore;
  start(): Promise<void>;
  stop(): Promise<void>;
};

export function createWebClient(options: WebClientOptions = {}): WebClientHandle {
  const store = new MemoryEngineStore();
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
    sessionId: options.sessionId ?? "session_web",
    reflexModules: productionReflexes,
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
