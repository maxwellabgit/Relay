import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { createNodeHarness } from "@relay/adapter-node";
import { localOnlyPolicy } from "@relay/contracts";
import { readFixture, runRecordedAudio, runReplay } from "@relay/testkit";

const fixture = resolve(process.cwd(), "fixtures/public/transcripts/acronym-basic.jsonl");
const recorded = resolve(process.cwd(), "fixtures/public/transcripts/recorded-commitment.segments.jsonl");

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

  it("replays a prepared audio file without turning listening on", async () => {
    const prepared = readFixture(recorded);
    expect(prepared.some((event) => event.type === "segment.final" && event.segment.origin === "audio_file")).toBe(
      true,
    );
    const harness = await createNodeHarness({ sessionId: "recorded_session" });
    try {
      await harness.client.start();
      expect((await harness.client.getSnapshot()).listening).toBe(false);
      const result = await runRecordedAudio({
        fixturePath: recorded,
        speed: 0,
        engine: harness.engine,
        sessionId: "recorded_session",
        captureSessionId: "capture_recorded_1",
      });
      const snap = await harness.client.getSnapshot();
      const text = "We promised to send the release report tomorrow.";
      expect(result.finals).toBe(1);
      expect(result.events.some((event) => event.type === "segment.interim")).toBe(true);
      expect(result.events.some((event) => event.type === "segment.final" && event.segment.text === text)).toBe(
        true,
      );
      expect(snap.listening).toBe(false);
      const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
      const sha256 = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
      const artifactId = `artifact_${sha256.slice(0, 24)}`;
      const stored = snap.sourceSegments.find((segment) => segment.origin === "audio_file");
      expect(stored?.text).toBe(`[artifact:${artifactId}]`);
      expect(stored?.text).not.toContain("release report");
      const bytes = await harness.artifacts.get({ artifactId, sha256, policy: localOnlyPolicy() });
      expect(new TextDecoder().decode(bytes)).toBe(text);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
