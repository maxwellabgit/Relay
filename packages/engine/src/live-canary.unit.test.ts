import { describe, expect, it } from "vitest";
import type { JudgmentPort, JudgmentResponse } from "@relay/contracts";
import { JevHealthTracker } from "./jev-health.js";
import { runPackagedLiveCanary } from "./live-canary.js";

function okResponse(): JudgmentResponse {
  return {
    ok: true,
    success: {
      model: "jev-latest",
      answers: { useful: { type: "noul", probabilityYes: 0.9 } },
      inputTokens: 1,
      outputTokens: 1,
      elapsedMs: 4,
      providerRequestId: "req_canary",
      transport: {
        configured: true,
        networkAttempted: true,
        attempts: 1,
        status: 200,
        category: "ok",
        requestBytes: 10,
        responseBytes: 10,
        latencyMs: 4,
        retryCount: 0,
        degradedReason: null,
      },
    },
  };
}

describe("packaged live canary", () => {
  it("records a receipt and moves health to healthy_live only after all three kinds", async () => {
    const kinds: string[] = [];
    const port: JudgmentPort = {
      async judge(request) {
        kinds.push(String(request.state && (request.state as { kind?: string }).kind));
        return okResponse();
      },
    };
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("configured_not_tested");
    const receipt = await runPackagedLiveCanary(port, tracker, new AbortController().signal);
    expect(kinds).toEqual(["noul", "choice", "score"]);
    expect(receipt.ok).toBe(true);
    expect(receipt.evidence).toBe("packaged_app");
    expect(receipt.rows).toHaveLength(3);
    expect(receipt.health.state).toBe("healthy_live");
    expect(tracker.state.state).toBe("healthy_live");
  });

  it("does not call noteLiveCanary when a kind fails", async () => {
    let calls = 0;
    const port: JudgmentPort = {
      async judge() {
        calls += 1;
        if (calls === 1) return okResponse();
        return { ok: false, failure: { category: "network", message: "typesafe_connection_failed" } };
      },
    };
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    const receipt = await runPackagedLiveCanary(port, tracker, new AbortController().signal);
    expect(receipt.ok).toBe(false);
    expect(receipt.health.state).toBe("unavailable");
    expect(tracker.state.state).not.toBe("healthy_live");
  });
});
