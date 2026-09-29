import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createNodeHarness } from "./create-client.js";

describe("activity observation persistence", () => {
  it("restores the timeline after the desktop store reopens", async () => {
    const databasePath = join(await mkdtemp(join(tmpdir(), "relay-activity-")), "state.sqlite");
    const first = await createNodeHarness({ databasePath });
    try {
      await first.client.start();
      expect((await first.client.execute({ type: "SetActivityObservation", patch: { enabled: true } })).ok).toBe(true);
      const ingested = await first.client.execute({
        type: "IngestActivityObservation",
        observation: {
          timestamp: "2026-09-29T15:00:00.000Z",
          source: { type: "windows", provider: "setwineventhook" },
          eventType: "foreground.changed",
          application: { processName: "cursor.exe", windowTitle: "relay — Cursor" },
        },
      });
      expect(ingested.ok).toBe(true);
      const live = await first.client.getSnapshot();
      expect(live.computerActivity?.episodes.some((item) => item.applications.includes("Cursor"))).toBe(true);
    } finally {
      await first.client.stop();
      first.close();
    }

    const second = await createNodeHarness({ databasePath });
    try {
      await second.client.start();
      const restored = await second.client.getSnapshot();
      expect(restored.computerActivity?.episodes.some((item) => item.applications.includes("Cursor"))).toBe(true);
      const replay = await second.client.execute({ type: "ReplayActivityFixture", fixture: "job-application" });
      expect(replay.summary).toBe("job_application");
      const shown = await second.client.getSnapshot();
      expect(shown.computerActivity?.example?.classification.label).toBe("job_application");
      expect(shown.computerActivity?.example?.caseCandidateLabel).toBe("Job Search");
    } finally {
      await second.client.stop();
      second.close();
    }
  });
});
