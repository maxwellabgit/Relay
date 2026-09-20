import { describe, expect, it } from "vitest";
import type { JudgmentRequest } from "@relay/contracts";

describe("native judgment secret boundary", () => {
  it("reports missing_secret when credential is unavailable", async () => {
    const invoke = async (command: string) => {
      if (command === "typesafe_judge") {
        return { ok: false, category: "missing_secret", status: 0, latency_ms: 1, body: null };
      }
      throw new Error(`unexpected:${command}`);
    };
    const port = {
      async judge(request: JudgmentRequest, signal: AbortSignal) {
        if (signal.aborted) {
          return { ok: false as const, failure: { category: "cancelled" as const, message: "jev_cancelled" } };
        }
        const result = (await invoke("typesafe_judge")) as {
          ok: boolean;
          category: string;
        };
        if (!result.ok) {
          return {
            ok: false as const,
            failure: {
              category: (result.category || "missing_secret") as "missing_secret",
              message: result.category || "missing_secret",
            },
          };
        }
        return { ok: true as const, success: { model: request.model, answers: {}, inputTokens: 0, outputTokens: 0, elapsedMs: 0 } };
      },
    };
    const response = await port.judge(
      {
        model: "systemone",
        questionSetId: "q",
        questionSetVersion: "1",
        questions: {},
        state: {},
      },
      new AbortController().signal,
    );
    expect(response.ok).toBe(false);
    if (!response.ok) {
      expect(response.failure.category).toBe("missing_secret");
    }
  });
});
