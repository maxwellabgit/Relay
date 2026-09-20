import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { createNodeHarness } from "./create-client.js";

describe("local model generation path", () => {
  it("answers a general ask through the local model port", async () => {
    const model: TextModelPort = {
      async generate(request) {
        expect(request.taskKind).toBe("direct_answer");
        expect(request.promptVersion).toBe("direct-answer.v1");
        expect(request.prompt).toContain("TCP");
        return {
          ok: true,
          text: "TCP is connection-oriented; UDP is connectionless.",
          model: "test-local",
          elapsedMs: 5,
        };
      },
    };
    const harness = createNodeHarness({
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
      text: "What is the difference between TCP and UDP?",
    });
    await waitFor(async () =>
      (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
    );
    const snap = await harness.client.getSnapshot();
    expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain("TCP is connection-oriented");
    expect(snap.status.find((s) => s.id === "model")?.detail).toBe("ready");
    expect(snap.feedItems.some((i) => i.summary.includes("No local result"))).toBe(false);
    await harness.client.stop();
    harness.close();
  });

  it("keeps the engine alive when the local model fails", async () => {
    const harness = createNodeHarness({
      model: {
        async generate() {
          return { ok: false, failureReason: "model_unavailable" };
        },
      },
    });
    await harness.client.start();
    await harness.client.execute({ type: "SubmitText", text: "Explain DNS briefly." });
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
