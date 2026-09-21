import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { createNodeHarness } from "./create-client.js";

describe("local model generation path", () => {
  it("answers a general ask through the local model port", async () => {
    const model: TextModelPort = {
      async generate(request) {
        expect(request.taskKind).toBe("direct_answer");
        expect(request.promptVersion).toBe("direct-answer.v1");
        expect(request.prompt).toContain("connection-oriented");
        return {
          ok: true,
          text: "TCP is connection-oriented; UDP is connectionless.",
          model: "test-local",
          elapsedMs: 5,
        };
      },
    };
    const harness = await createNodeHarness({
      model,
      judgments: {
        async judge() {
          return { ok: false, failure: { category: "missing_secret", message: "missing" } };
        },
      },
    });

    await harness.client.start();
    await harness.client.execute({
      type: "SubmitText",
      text: "What is the difference between connection-oriented and connectionless transport?",
    });
    await waitFor(async () => {
      const snap = await harness.client.getSnapshot();
      return (
        snap.feedItems.some((item) => item.kind === "answer") &&
        snap.waits.length === 0 &&
        snap.trace.some((line) => line.type === "answer.committed")
      );
    });
    const snap = await harness.client.getSnapshot();
    const ask = snap.feedItems.find((item) => item.kind === "ask");
    const answer = snap.feedItems.find((item) => item.kind === "answer");
    expect(answer?.summary).toContain("TCP is connection-oriented");
    expect(ask?.itemId).toMatch(/^feed_.+_ask$/);
    expect(answer?.itemId).toMatch(/^feed_.+_answer$/);
    expect(snap.waits).toHaveLength(0);
    expect(snap.status.find((s) => s.id === "model")?.detail).toBe("ready");
    expect(snap.feedItems.some((i) => i.summary.includes("No local result"))).toBe(false);
    expect(snap.feedItems.filter((i) => i.kind === "answer")).toHaveLength(1);
    expect(snap.trace.some((line) => line.type === "model.requested")).toBe(true);
    expect(snap.trace.some((line) => line.type === "answer.committed")).toBe(true);
    await harness.client.stop();
    harness.close();
  });

  it("keeps the engine alive when the local model fails", async () => {
    const harness = await createNodeHarness({
      model: {
        async generate() {
          return { ok: false, failureReason: "model_unavailable" };
        },
      },
    });
    await harness.client.start();
    await harness.client.execute({
      type: "SubmitText",
      text: "Explain how recursive name resolution works in practice without inventing tools.",
    });
    await waitFor(async () =>
      (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
    );
    const snap = await harness.client.getSnapshot();
    expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toBe("No local result for this Ask.");
    await harness.client.stop();
    harness.close();
  });
});

async function waitFor(predicate: () => Promise<boolean>): Promise<void> {
  for (let i = 0; i < 80; i += 1) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 25));
  }
  throw new Error("timeout");
}
