import { describe, expect, it } from "vitest";
import { createNodeHarness } from "./create-client.js";

function micSegment(sessionId: string, segmentId: string, text: string, sequence: number) {
  return {
    schemaVersion: 1 as const,
    sourceId: "mic",
    sessionId,
    segmentId,
    revision: 1,
    sequence,
    startMs: 0,
    endMs: 500,
    speakerKey: null,
    speakerConfidence: null,
    text,
    textConfidence: null,
    final: true as const,
    origin: "microphone" as const,
    cursor: null,
  };
}

describe("listen intake gate", () => {
  it("rejects observed segments while Listen is OFF", async () => {
    const harness = await createNodeHarness({ sessionId: "session_listen_off" });
    await harness.client.start();
    const caseId = await harness.engine.ingestFinalSegment(
      micSegment("session_listen_off", "seg_off", "We need the API ready", 1),
      false,
    );
    expect(caseId).toBe("");
    const snap = await harness.client.getSnapshot();
    expect(snap.cases.length).toBe(0);
    await harness.client.stop();
    harness.close();
  });

  it("accepts observed segments while Listen is ON and keeps Ask working", async () => {
    const harness = await createNodeHarness({
      sessionId: "session_listen_on",
      model: {
        async generate() {
          return { ok: true, text: "typed answer", model: "t", elapsedMs: 1 };
        },
      },
    });
    await harness.client.start();
    await harness.client.execute({ type: "SetListening", enabled: true });
    const observed = await harness.engine.ingestFinalSegment(
      micSegment("session_listen_on", "seg_on", "We need the API ready", 1),
      false,
    );
    expect(observed).toMatch(/^case_/);
    await harness.client.execute({ type: "SubmitText", text: "What is DNS?" });
    await waitFor(async () => (await harness.client.getSnapshot()).listening === true);
    expect((await harness.client.getSnapshot()).listening).toBe(true);
    await harness.client.execute({ type: "SetListening", enabled: false });
    const rejected = await harness.engine.ingestFinalSegment(
      micSegment("session_listen_on", "seg_after", "Should not create a case", 2),
      false,
    );
    expect(rejected).toBe("");
    await harness.client.stop();
    harness.close();
  });

  it("deduplicates identical final segment revisions", async () => {
    const harness = await createNodeHarness({ sessionId: "session_dedupe" });
    await harness.client.start();
    await harness.client.execute({ type: "SetListening", enabled: true });
    const segment = micSegment("session_dedupe", "seg_dup", "Duplicate final", 1);
    const first = await harness.engine.ingestFinalSegment(segment, false);
    const second = await harness.engine.ingestFinalSegment(segment, false);
    expect(first).toMatch(/^case_/);
    expect(second).toBe("");
    await harness.client.stop();
    harness.close();
  });
});

async function waitFor(predicate: () => Promise<boolean>): Promise<void> {
  for (let i = 0; i < 40; i += 1) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 20));
  }
}
