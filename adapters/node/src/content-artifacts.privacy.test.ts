import { readdirSync, readFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { localOnlyPolicy } from "@relay/contracts";
import { createNodeHarness } from "./create-client.js";
import { MemoryArtifactStore } from "./memory-artifacts.js";

const SENTINEL = "PRIVACY_SENTINEL_feed_7f3a9c2e_UNIQUE";

describe("content artifact privacy boundary", () => {
  it("keeps transcript sentinel out of events.jsonl and sqlite metadata", async () => {
    const root = mkdtempSync(join(tmpdir(), "relay-feed-privacy-"));
    const dbPath = join(root, "state.sqlite");
    const runsRoot = join(root, "runs");
    try {
      const harness = createNodeHarness({
        databasePath: dbPath,
        runsRoot,
        sessionId: "session_privacy",
      });
      await harness.client.start();
      await harness.client.execute({ type: "SetListening", enabled: true });
      await harness.engine.ingestFinalSegment(
        {
          schemaVersion: 1,
          sourceId: "mic",
          sessionId: "session_privacy",
          segmentId: "seg_privacy_1",
          revision: 1,
          sequence: 1,
          origin: "microphone",
          speakerKey: null,
          speakerConfidence: null,
          startMs: 0,
          endMs: 1000,
          text: SENTINEL,
          textConfidence: null,
          final: true,
          cursor: null,
        },
        false,
      );
      await new Promise((r) => setTimeout(r, 80));

      const runDirs = readdirSync(runsRoot).filter((d) => d.startsWith("run_"));
      expect(runDirs.length).toBeGreaterThan(0);
      const eventsText = readFileSync(join(runsRoot, runDirs[0]!, "events.jsonl"), "utf8");
      expect(eventsText).not.toContain(SENTINEL);

      const domain = await harness.store.listDomainEvents(200);
      expect(JSON.stringify(domain)).not.toContain(SENTINEL);

      const feedRecords = await harness.store.listFeedItemRecords();
      expect(JSON.stringify(feedRecords)).not.toContain(SENTINEL);

      const sqliteBytes = readFileSync(dbPath);
      expect(sqliteBytes.includes(Buffer.from(SENTINEL))).toBe(false);

      const recovered = await harness.artifacts.get(
        await harness.artifacts.put(new TextEncoder().encode(SENTINEL), localOnlyPolicy()),
      );
      expect(new TextDecoder().decode(recovered)).toBe(SENTINEL);

      await harness.client.stop();
      harness.close();
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it("content-addresses payloads and rejects corruption or missing artifacts", async () => {
    const store = new MemoryArtifactStore();
    const bytes = new TextEncoder().encode(SENTINEL);
    const a = await store.put(bytes, localOnlyPolicy());
    const b = await store.put(bytes, localOnlyPolicy());
    expect(a.artifactId).toBe(b.artifactId);
    expect(new TextDecoder().decode(await store.get(a))).toBe(SENTINEL);
    store.corrupt(a.artifactId, new TextEncoder().encode("tampered"));
    await expect(store.get(a)).rejects.toThrow("artifact_hash_mismatch");
    store.delete(a.artifactId);
    await expect(store.get(a)).rejects.toThrow(/artifact_missing/);
  });
});
