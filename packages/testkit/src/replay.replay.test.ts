import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { createNodeHarness } from "@relay/adapter-node";
import { readFixture, runReplay } from "@relay/testkit";

const fixture = resolve(process.cwd(), "fixtures/public/transcripts/acronym-basic.jsonl");

describe("transcript replay", () => {
  it("emits identical event contents at speed 0 and speed 10", async () => {
    const events0 = readFixture(fixture);
    const events10 = readFixture(fixture);
    expect(events0).toEqual(events10);
    expect(events0.some((e) => e.type === "segment.final")).toBe(true);
    expect(events0.some((e) => e.type === "segment.speaker_revised")).toBe(true);
  });

  it("streams finals into the engine and produces acronym outcomes", async () => {
    const harness = await createNodeHarness({ sessionId: "replay_session" });
    try {
      await harness.client.start();
      const result = await runReplay({
        fixturePath: fixture,
        speed: 0,
        engine: harness.engine,
        sessionId: "replay_session",
        captureSessionId: "capture_replay_1",
      });
      const snap = await harness.client.getSnapshot();
      expect(result.finals).toBe(2);
      expect(snap.listening).toBe(false);
      expect(snap.sourceSegments.length).toBeGreaterThan(0);
      expect(snap.cases.length + snap.feedItems.length).toBeGreaterThan(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
