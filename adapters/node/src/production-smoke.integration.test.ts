import { mkdir, mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { TauriEngineStore } from "@relay/adapter-tauri/engine-store";
import { createProductionIds, createRelayClientFromEngine, RelayEngine } from "@relay/engine";
import { createProductionReflexes } from "@relay/reflexes";
import { createFileTraceSink } from "./file-trace.js";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { openSqliteEngineStore } from "./sqlite-store.js";
import { sqliteStoreInvoke } from "./store-invoke.js";

const SENTINEL = "PRIVACY_SENTINEL_7f3a9c2e";

describe("windows production composition smoke", () => {
  it("survives restart with glossary and birthday through Tauri store transport", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-smoke-"));
    const appData = join(root, "LOCALAPPDATA", "RELAY");
    await mkdir(appData, { recursive: true });
    const databasePath = join(appData, "state.sqlite");
    const diagnosticsRoot = join(appData, "diagnostics");
    const runsRoot = join(diagnosticsRoot, "runs");
    await mkdir(runsRoot, { recursive: true });
    process.env.LOCALAPPDATA = join(root, "LOCALAPPDATA");
    process.env.RELAY_DIAGNOSTICS_ROOT = diagnosticsRoot;

    const first = await openComposition(databasePath, runsRoot, diagnosticsRoot);
    try {
      await first.client.start();
      const glossary = await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      expect(glossary.ok).toBe(true);
      const birthday = await first.client.execute({
        type: "CaptureBirthday",
        displayName: "Rasmi Pandey",
        date: "01-04",
        confirmed: true,
      });
      expect(birthday.ok).toBe(true);
      const snap = await first.client.getSnapshot();
      expect(snap.runtime.storageAdapter).toBe("sqlite");
      expect(snap.runtime.logPath.length).toBeGreaterThan(0);
    } finally {
      await first.client.stop();
      first.close();
    }

    const second = await openComposition(databasePath, runsRoot, diagnosticsRoot);
    try {
      await second.client.start();
      await second.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await second.client.getSnapshot();
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer Suggested Retail Price",
      );
      const birthday = await second.store.learning.listMemories();
      expect(birthday.some((item) => item.kind === "birthday")).toBe(true);
      const runDirs = await readdir(runsRoot);
      expect(runDirs.length).toBeGreaterThan(0);
      const eventsPath = join(runsRoot, runDirs[0]!, "events.jsonl");
      const text = await readFile(eventsPath, "utf8");
      expect(text.length).toBeGreaterThan(0);
      expect(text.includes(SENTINEL)).toBe(false);
      expect(text.toLowerCase().includes("manufacturer suggested")).toBe(false);
      const latest = JSON.parse(
        await readFile(join(diagnosticsRoot, "latest.json"), "utf8"),
      ) as { runId?: string; runDir?: string };
      expect(latest.runId).toMatch(/^run_/);
      expect(runDirs.some((name) => latest.runDir?.includes(name))).toBe(true);
    } finally {
      await second.client.stop();
      second.close();
      await rm(root, { recursive: true, force: true });
    }
  });
});

async function openComposition(databasePath: string, runsRoot: string, diagnosticsRoot: string) {
  const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(databasePath));
  const sqlite = await openSqliteEngineStore(databasePath, artifacts);
  const store = new TauriEngineStore(sqliteStoreInvoke(sqlite), artifacts);
  const ids = createProductionIds();
  const engine = new RelayEngine({
    store,
    artifacts,
    judgments: {
      async judge() {
        return { ok: false, failure: { category: "missing_secret", message: "missing key" } };
      },
    },
    model: {
      async generate() {
        return { ok: false, failureReason: "model_disabled" };
      },
    },
    clock: { now: () => new Date() },
    ids,
    sessionId: ids.next("session"),
    reflexModules: createProductionReflexes(store.learning),
    storageDetail: "sqlite",
    jevStatus: { ok: false, detail: "missing key" },
    modelStatus: { ok: false, detail: "disabled" },
    mode: "live",
    gitCommit: "smoke",
    trace: createFileTraceSink({ runsRoot, diagnosticsRoot, commit: "smoke", profile: "smoke" }),
  });
  return {
    client: createRelayClientFromEngine(engine),
    store,
    close: () => sqlite.close(),
  };
}

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 4000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error("timeout");
}
