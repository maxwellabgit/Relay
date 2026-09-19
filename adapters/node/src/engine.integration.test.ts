import { describe, expect, it } from "vitest";
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

      const ask = await client.execute({ type: "SubmitText", text: "What does API mean?" });
      expect(ask.ok).toBe(true);
      expect(ask.caseId).toBeTruthy();

      await waitFor(async () => {
        const snap = await client.getSnapshot();
        return snap.feedItems.some((i) => i.caseId === ask.caseId) && snap.listening;
      });

      const snap = await client.getSnapshot();
      expect(snap.listening).toBe(true);
      expect(snap.status.find((s) => s.id === "engine")?.ok).toBe(true);
      expect(snap.sourceSegments.length).toBeGreaterThan(0);
    } finally {
      await client.stop();
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
});
