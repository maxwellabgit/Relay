import type { TextModelPort, TranscriptSegmentV1 } from "@relay/contracts";
import { describe, expect, it } from "vitest";
import { createNodeHarness } from "./create-client.js";

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 12_000): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error("timeout");
}

describe("reviewed note, fact, and recommendation semantics", () => {
  it("stores distinct kinds and polished feed copy", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "add a note: buy filters" });
      await harness.client.execute({ type: "SubmitText", text: "remember a fact: MSRP means list price" });
      await harness.client.execute({ type: "SubmitText", text: "next action: call the supplier" });
      await harness.client.execute({ type: "SubmitText", text: "what should I do next?" });
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.summary === "Saved note: buy filters") &&
          snap.feedItems.some((item) => item.summary === "Remembered: MSRP means list price") &&
          snap.feedItems.some((item) => item.summary === "Next: call the supplier") &&
          snap.feedItems.some((item) => item.summary === "What should I do next?")
        );
      });
      const memories = await harness.store.learning.listMemories();
      const note = memories.find((memory) => memory.kind === "note");
      const fact = memories.find((memory) => memory.kind === "fact");
      const next = memories.filter((memory) => memory.kind === "recommendation");
      expect(note?.value.text).toBe("buy filters");
      expect(fact?.value.text).toBe("MSRP means list price");
      expect(next.map((memory) => memory.value.text ?? "").sort()).toEqual(["", "call the supplier"]);
      expect(memories.filter((memory) => memory.kind === "note")).toHaveLength(1);
      const snap = await harness.client.getSnapshot();
      expect(snap.memories.find((memory) => memory.kind === "note")?.fields.text).toBe("buy filters");
      expect(snap.memories.some((memory) => memory.key.includes("note:"))).toBe(true);
      expect(snap.feedItems.some((item) => item.summary.startsWith("note:"))).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("keeps one memory when the same explicit note is submitted twice", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "note: buy filters" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.some((item) => item.summary === "Saved note: buy filters"),
      );
      await harness.client.execute({ type: "SubmitText", text: "note: buy filters" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.filter((item) => item.summary === "Saved note: buy filters")
          .length >= 2,
      );
      const notes = (await harness.store.learning.listMemories()).filter((memory) => memory.kind === "note");
      expect(notes).toHaveLength(1);
      expect(notes[0]?.value.text).toBe("buy filters");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("does not create a second case when the same scripted segment is replayed", async () => {
    const harness = await createNodeHarness({ sessionId: "session_replay" });
    const segment: TranscriptSegmentV1 = {
      schemaVersion: 1,
      sourceId: "fixture",
      sessionId: "session_replay",
      segmentId: "seg_same",
      revision: 1,
      sequence: 1,
      startMs: 0,
      endMs: 1000,
      speakerKey: null,
      speakerConfidence: null,
      text: "We should check the API before launch.",
      textConfidence: 1,
      final: true,
      origin: "scripted_transcript",
      cursor: null,
    };
    try {
      await harness.client.start();
      const first = await harness.engine.ingestReplayFinalSegment(segment);
      const second = await harness.engine.ingestReplayFinalSegment(segment);
      expect(first).not.toBe("");
      expect(second).toBe("");
      const bound = (await harness.store.listDomainEvents(40)).filter((event) => event.type === "source.case_bound");
      expect(bound).toHaveLength(1);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("does not publish a model answer after cancel", async () => {
    let entered!: () => void;
    const started = new Promise<void>((resolve) => {
      entered = resolve;
    });
    const model: TextModelPort = {
      async generate(_request, signal) {
        entered();
        await new Promise<void>((resolve) => {
          if (!signal || signal.aborted) {
            resolve();
            return;
          }
          signal.addEventListener("abort", () => resolve(), { once: true });
        });
        return { ok: true, text: "SHOULD_NOT_APPEAR", model: "test-local", elapsedMs: 1 };
      },
    };
    const harness = await createNodeHarness({ model });
    try {
      await harness.client.start();
      await harness.client.execute({
        type: "SubmitText",
        text: "What is the difference between connection-oriented and connectionless transport?",
      });
      await started;
      const cancelled = await harness.client.execute({ type: "CancelActive" });
      expect(cancelled.ok).toBe(true);
      expect(cancelled.caseId).toBeTruthy();
      await new Promise((resolve) => setTimeout(resolve, 50));
      const record = await harness.store.getCase(cancelled.caseId ?? "");
      expect(record?.status).toBe("cancelled");
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.summary.includes("SHOULD_NOT_APPEAR"))).toBe(false);
      expect(snap.feedItems.filter((item) => item.kind === "answer")).toHaveLength(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
