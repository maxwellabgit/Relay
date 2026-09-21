import { describe, expect, it, vi } from "vitest";
import type { JudgmentPort } from "@relay/contracts";
import { createResolveAcronymModule } from "@relay/reflexes";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 5000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error("timeout");
}

function ambiguous() {
  return createResolveAcronymModule({
    glossary: {
      exactUser: async () => null,
      exactProject: async () => null,
      exactBundled: async (token) =>
        token === "API" ? "Application Programming Interface" : null,
      searchWindow: async (token) => (token === "BESS" ? ["Battery Energy Storage", "Bessemer"] : []),
    },
  });
}

describe("hosted processing authority", () => {
  it("defaults off and blocks Jev without substituting local model", async () => {
    const judge = vi.fn(async () =>
      recordedSuccess({
        expansion: {
          type: "choice",
          choice: "Battery Energy Storage",
          probabilities: { "Battery Energy Storage": 0.9, Bessemer: 0.05, no_match: 0.05 },
          confidence: 0.9,
        },
        useful: { type: "noul", probabilityYes: 0.8 },
      }),
    );
    const judgments: JudgmentPort = { judge };
    const harness = await createNodeHarness({ judgments, reflexModules: [ambiguous()] });
    await harness.store.setHostedProcessingEnabled(false);
    await harness.client.start();
    try {
      expect(await harness.store.getHostedProcessingEnabled()).toBe(false);
      expect(await harness.store.getListening("session_test")).toBe(false);

      await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.gate?.reasonCode === "hosted_processing_disabled";
      });
      const snap = await harness.client.getSnapshot();
      expect(judge).not.toHaveBeenCalled();
      expect(snap.hostedProcessingEnabled).toBe(false);
      expect(snap.gate?.reasonCode).toBe("hosted_processing_disabled");
      expect(snap.feedItems.some((item) => item.kind === "answer")).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("allows Jev only after SetHostedProcessing enable, independent of Listening", async () => {
    const judge = vi.fn(async () =>
      recordedSuccess({
        expansion: {
          type: "choice",
          choice: "Battery Energy Storage",
          probabilities: { "Battery Energy Storage": 0.9, Bessemer: 0.05, no_match: 0.05 },
          confidence: 0.9,
        },
        useful: { type: "noul", probabilityYes: 0.8 },
      }),
    );
    const judgments: JudgmentPort = { judge };
    const harness = await createNodeHarness({ judgments, reflexModules: [ambiguous()] });
    await harness.client.start();
    try {
      await harness.client.execute({ type: "SetHostedProcessing", enabled: true });
      expect(await harness.store.getHostedProcessingEnabled()).toBe(true);
      expect(await harness.store.getListening("session_test")).toBe(false);

      await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => judge.mock.calls.length > 0);
      expect(judge).toHaveBeenCalled();
      const snap = await harness.client.getSnapshot();
      expect(snap.hostedProcessingEnabled).toBe(true);
      expect(snap.listening).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
