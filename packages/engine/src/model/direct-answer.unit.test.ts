import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { draftDirectAnswer } from "./direct-answer.js";

function scripted(replies: readonly string[]): TextModelPort & { readonly calls: () => number } {
  let count = 0;
  return {
    calls: () => count,
    async generate(_request, signal) {
      if (signal.aborted) return { ok: false, failureReason: "cancelled" };
      const text = replies[count] ?? "";
      count += 1;
      return { ok: true, text, model: "double", elapsedMs: 1 };
    },
  };
}

describe("direct answer draft", () => {
  it("repairs an empty answer once", async () => {
    const model = scripted(["", "Use the release checklist."]);
    const result = await draftDirectAnswer({
      model,
      ask: "What should I do next?",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(result).toEqual({ ok: true, text: "Use the release checklist.", attempts: 2 });
  });

  it("keeps a valid first answer", async () => {
    const model = scripted(["Ship the candidate."]);
    const result = await draftDirectAnswer({
      model,
      ask: "What next?",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(1);
    expect(result.ok).toBe(true);
  });

  it("rejects an answer that only repeats the instructions", async () => {
    const model = scripted(["Do not invent tool calls.", "Do not invent acronym definitions."]);
    const result = await draftDirectAnswer({
      model,
      ask: "What next?",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(result).toEqual({ ok: false, reason: "invalid_response", attempts: 2 });
  });
});
