import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { TauriEngineStore } from "@relay/adapter-tauri/engine-store";
import { createProductionIds, createRelayClientFromEngine, RelayEngine } from "@relay/engine";
import { createProductionReflexes } from "@relay/reflexes";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { openSqliteEngineStore } from "./sqlite-store.js";
import { sqliteStoreInvoke } from "./store-invoke.js";

const SENTINEL = "WORKFLOW_PAGE_SENTINEL_9c1e";

describe("workflow observation persistence", () => {
  it("keeps an episode across restart and stores page text outside sqlite", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-workflow-"));
    const databasePath = join(root, "state.sqlite");
    const writes: string[] = [];
    const open = async () => {
      const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(databasePath));
      const sqlite = await openSqliteEngineStore(databasePath, artifacts);
      const store = new TauriEngineStore(sqliteStoreInvoke(sqlite), artifacts);
      const ids = createProductionIds();
      const engine = new RelayEngine({
        store,
        artifacts,
        judgments: { async judge() { return { ok: false, failure: { category: "missing_secret", message: "missing key" } }; } },
        model: { async generate() { return { ok: false, failureReason: "model_disabled" }; } },
        clock: { now: () => new Date("2026-09-28T18:00:00.000Z") },
        ids,
        sessionId: ids.next("session"),
        reflexModules: createProductionReflexes(store.learning),
        storageDetail: "sqlite",
        mode: "recorded",
        workflowFiles: {
          async readText() { return "Alex Rivera\nBuilt production ML inference services for ranking."; },
          async writeDraft(name, body) { writes.push(body); return join(root, name); },
        },
      });
      return { client: createRelayClientFromEngine(engine), close: () => sqlite.close() };
    };

    const first = await open();
    try {
      await first.client.start();
      expect((await first.client.execute({
        type: "Workflow",
        action: "configure",
        setupComplete: true,
        paused: false,
        windowsEnabled: true,
        chromeEnabled: true,
        pageContentEnabled: true,
        allowedSites: ["jobs.example.com"],
      })).ok).toBe(true);
      await first.client.execute({
        type: "Workflow",
        action: "ingest",
        signals: [
          {
            observedAt: "2026-09-28T18:00:00.000Z",
            sourceType: "browser",
            provider: "chrome",
            eventType: "page.semantic",
            url: "https://jobs.example.com/jobs/1",
            title: "Senior ML Engineer — Example",
            excerpt: `Requirements include inference services. ${SENTINEL}`,
          },
          {
            observedAt: "2026-09-28T18:05:00.000Z",
            sourceType: "desktop",
            provider: "windows",
            eventType: "window.focus",
            application: "WINWORD.EXE",
            title: "master_resume.docx",
          },
        ],
      });
      const snap = await first.client.getSnapshot();
      expect(snap.workflow?.episodes[0]?.classification).toBe("job_application");
      const { readFile } = await import("node:fs/promises");
      const database = await readFile(databasePath);
      expect(database.includes(Buffer.from(SENTINEL))).toBe(false);
    } finally {
      await first.client.stop();
      first.close();
    }

    const second = await open();
    try {
      await second.client.start();
      const snap = await second.client.getSnapshot();
      expect(snap.workflow?.episodes).toHaveLength(1);
      expect(snap.workflow?.settings.chromeEnabled).toBe(true);
      expect(JSON.stringify(snap.workflow).includes(SENTINEL)).toBe(false);
    } finally {
      await second.client.stop();
      second.close();
      await rm(root, { recursive: true, force: true });
    }
  });
});
