import { describe, expect, it } from "vitest";
import { createTypeSafeJudgmentPort, parseTypeSafeBody } from "./typesafe-judgment.js";

const request = {
  questionSetId: "judgment.remember",
  questionSetVersion: "1",
  model: "jev-1.13.0",
  state: { token: "MSRP" },
  questions: {
    remember: {
      type: "noul" as const,
      instructions: "Should this acronym be stored?",
    },
  },
};

describe("TypeSafe Jev client", () => {
  it("does not call the network when the API key is missing", async () => {
    let called = false;
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => null,
      fetchImpl: async () => {
        called = true;
        throw new Error("should not fetch");
      },
    });
    const result = await port.judge(request, new AbortController().signal);
    expect(called).toBe(false);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.failure.category).toBe("missing_secret");
  });

  it("posts the System One body and maps a noul probability", async () => {
    let seenUrl = "";
    let seenAuth = "";
    let seenBody = "";
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      retryDelayMs: 0,
      maxAttempts: 1,
      fetchImpl: async (url, init) => {
        seenUrl = String(url);
        seenAuth = new Headers(init?.headers).get("Authorization") ?? "";
        seenBody = String(init?.body ?? "");
        return new Response(
          JSON.stringify({
            model: "jev-1.13.0",
            answers: { remember: { type: "noul", noul: 0.91 } },
            usage: { input_tokens: 12, output_tokens: 1 },
          }),
          { status: 200 },
        );
      },
    });

    const result = await port.judge(request, new AbortController().signal);
    expect(seenUrl).toBe("https://api.typesafe.ai/v1/systemone");
    expect(seenAuth).toBe("Bearer test-key");
    expect(seenBody).toContain('"type":"noul"');
    expect(seenBody).not.toContain("test-key");
    expect(result.ok).toBe(true);
    if (result.ok) {
      const answer = result.success.answers.remember;
      expect(answer?.type).toBe("noul");
      if (answer?.type === "noul") expect(answer.probabilityYes).toBe(0.91);
    }
  });

  it("rejects a noul outside the unit interval", () => {
    const parsed = parseTypeSafeBody(
      JSON.stringify({ model: "jev-1.13.0", answers: { remember: { type: "noul", noul: 2 } } }),
      1,
    );
    expect(parsed.ok).toBe(false);
  });
});
