import type { JudgmentPort, GitHubReadPort, PublicSearchPort, TextModelPort } from "@relay/contracts";
import { TauriEngineStore } from "@relay/adapter-tauri/engine-store";
import {
  createRelayClientFromEngine,
  grantAccountFor,
  HostedGrantLedger,
  recordedHarnessGrant,
  RelayEngine,
  type EngineDeps,
} from "@relay/engine";
import { createProductionReflexes } from "@relay/reflexes";
import { RecordedJudgmentPort, recordedSuccess } from "@relay/testkit";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DecisionArtifactStore, decisionArtifactRoot } from "./decision-artifacts.js";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { MemoryArtifactStore } from "./memory-artifacts.js";
import { openSqliteEngineStore } from "./sqlite-store.js";
import { sqliteStoreInvoke } from "./store-invoke.js";
import { createFileTraceSink } from "./file-trace.js";

export type NodeHarnessOptions = {
  readonly sessionId?: string;
  readonly databasePath?: string;
  readonly runsRoot?: string;
  readonly clock?: EngineDeps["clock"];
  readonly ids?: EngineDeps["ids"];
  readonly judgments?: JudgmentPort;
  readonly model?: TextModelPort;
  readonly publicSearch?: PublicSearchPort;
  readonly github?: GitHubReadPort;
  readonly reflexModules?: EngineDeps["reflexModules"];
  readonly episodeDefinitions?: EngineDeps["episodeDefinitions"];
  readonly durableDecisionArtifacts?: boolean;
  /** Recorded harnesses seed a session grant. Production callers leave this unset. */
  readonly seedDisclosureGrant?: boolean;
  readonly audioStatus?: EngineDeps["audioStatus"];
};

export async function createNodeHarness(options: NodeHarnessOptions = {}) {
  const databasePath = options.databasePath ?? ":memory:";
  const runsRoot = options.runsRoot ?? join(tmpdir(), "relay-runs");
  const runId = `run_${Date.now().toString(36)}`;
  const artifacts =
    options.durableDecisionArtifacts
      ? new DecisionArtifactStore(decisionArtifactRoot(runsRoot, runId))
      : databasePath !== ":memory:"
        ? new FileArtifactStore(fileArtifactRootForDatabase(databasePath))
        : new MemoryArtifactStore();
  const sqlite = await openSqliteEngineStore(databasePath, artifacts);
  const store = new TauriEngineStore(sqliteStoreInvoke(sqlite), artifacts);
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

  const model: TextModelPort =
    options.model ??
    ({
      async generate() {
        return { ok: false, failureReason: "model_disabled" };
      },
    } satisfies TextModelPort);

  const deps: EngineDeps = {
    store,
    artifacts,
    judgments,
    model,
    clock,
    ids,
    sessionId: options.sessionId ?? "session_test",
    reflexModules: options.reflexModules ?? createProductionReflexes(store.learning),
    ...(options.episodeDefinitions ? { episodeDefinitions: options.episodeDefinitions } : {}),
    ...(options.publicSearch ? { publicSearch: options.publicSearch } : {}),
    ...(options.github ? { github: options.github } : {}),
    storageDetail: "sqlite",
    jevStatus: { ok: true, detail: "recorded" },
    modelStatus: options.model
      ? { ok: true, detail: "ready" }
      : { ok: false, detail: "disabled" },
    ...(options.audioStatus ? { audioStatus: options.audioStatus } : {}),
    mode: "recorded",
    gitCommit: "test",
    trace: createFileTraceSink(runsRoot, runId),
  };

  const engine = new RelayEngine(deps);
  const client = createRelayClientFromEngine(engine);

  // Recorded harnesses exercise Jev paths with an explicit session grant.
  // Production desktop defaults to OFF. The boolean alone does not authorize disclosure.
  await sqlite.setHostedProcessingEnabled(true);
  if (options.seedDisclosureGrant !== false) {
    await new HostedGrantLedger(grantAccountFor(store)).save(
      recordedHarnessGrant(options.sessionId ?? "session_test"),
      clock.now().toISOString(),
    );
  }

  // Bind sqlite transaction boundary onto the invoke-backed store facade.
  store.runInTransaction = <T>(work: () => Promise<T>) => sqlite.runInTransaction(work);

  return {
    client,
    engine,
    store,
    artifacts,
    close: () => sqlite.close(),
  };
}

export { openSqliteEngineStore, SqliteEngineStore } from "./sqlite-store.js";
