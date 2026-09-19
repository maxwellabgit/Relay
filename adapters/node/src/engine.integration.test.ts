import { describe, expect, it } from "vitest";
import type { JudgmentPort } from "@relay/contracts";
import { createNodeHarness } from "./create-client.js";

async function waitFor(
  predicate: () => Promise<boolean>,
  timeoutMs = 2000,
): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 20));
  }
  throw new Error("timeout");
}

describe("autonomous engine", () => {
  it("starts a queue loop independent of React and processes Ask without pausing Listen", async () => {
    const harness = createNodeHarness({
      ids: (() => {
        let n = 0;
        return { next: (prefix: string) => `${prefix}_${++n}` };
      })(),
    });
    const { client } = harness;
    try {
      await client.start();
      const listen = await client.execute({ type: "SetListening", enabled: true });
      expect(listen.ok).toBe(true);

      const ask = await client.execute({
        type: "SubmitText",
        text: "What does API mean?",
      });
      expect(ask.ok).toBe(true);
      expect(ask.caseId).toBeTruthy();

      await waitFor(async () => {
        const snap = await client.getSnapshot();
        return (
          snap.feedItems.some((i) => i.kind === "answer" || i.kind === "finding") &&
          snap.listening
        );
      });

      const snap = await client.getSnapshot();
      expect(snap.listening).toBe(true);
      expect(snap.feedItems.filter((i) => i.kind === "ask")).toHaveLength(1);
      expect(snap.feedItems.filter((i) => i.kind === "finding")).toHaveLength(0);
      expect(snap.feedItems.filter((i) => i.kind === "answer")).toHaveLength(1);
      expect(snap.feedItems.find((i) => i.kind === "answer")?.summary).toContain(
        "Application Programming Interface",
      );
      expect(snap.status.find((s) => s.id === "model")?.detail).toBe("disabled");
      expect(snap.status.find((s) => s.id === "storage")?.detail).toBe("sqlite");
    } finally {
      await client.stop();
      harness.close();
    }
  });

  it("recommends a search task for an unknown acronym instead of inventing a definition", async () => {
    const harness = createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({
        type: "SubmitText",
        text: "What does MSRP mean?",
      });
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.feedItems.some((i) => i.kind === "task");
      });
      const snap = await harness.client.getSnapshot();
      const tasks = snap.feedItems.filter((i) => i.kind === "task");
      const answers = snap.feedItems.filter((i) => i.kind === "answer" || i.kind === "finding");
      expect(tasks).toHaveLength(1);
      expect(tasks[0]?.summary).toBe("Search online for the definition of MSRP");
      expect(answers).toHaveLength(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("persists final source events before scheduling case work", async () => {
    const harness = createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "BESS glossary check" });
      await waitFor(async () => (await harness.client.getSnapshot()).feedItems.length > 0);
      const segments = await harness.store.listSourceSegments("session_test");
      expect(segments).toHaveLength(1);
      expect(segments[0]?.final).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("does not store memory when Jev has no key", async () => {
    const calls: string[] = [];
    const judgments: JudgmentPort = {
      async judge(request) {
        calls.push(request.questionSetId);
        return { ok: false, failure: { category: "missing_secret", message: "typesafe_key_missing" } };
      },
    };
    const harness = createNodeHarness({ judgments });
    try {
      await harness.client.start();
      const result = await harness.client.execute({ type: "RememberToken", token: "MSRP" });
      expect(result.ok).toBe(false);
      expect(result.summary).toBe("missing_secret");
      expect(calls).toEqual(["judgment.remember"]);
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.summary.startsWith("Remembered"))).toBe(false);
      expect(snap.activity.some((line) => line.message.includes("missing_secret"))).toBe(true);
      expect(snap.activity.some((line) => line.message.includes("not accepted"))).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("stores memory only after a Jev confidence interval clears the gate", async () => {
    const judgments: JudgmentPort = {
      async judge() {
        return {
          ok: true,
          success: {
            model: "jev-1.13.0",
            answers: { remember: { type: "noul", probabilityYes: 0.82 } },
            inputTokens: 1,
            outputTokens: 1,
            elapsedMs: 4,
          },
        };
      },
    };
    const harness = createNodeHarness({ judgments });
    try {
      await harness.client.start();
      const result = await harness.client.execute({ type: "RememberToken", token: "msrp" });
      expect(result.ok).toBe(true);
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.summary.startsWith("Remembered MSRP"))).toBe(true);
      expect(
        snap.activity.some((line) => line.eventType === "jev.accepted" && line.message.includes("0.18–0.82")),
      ).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
