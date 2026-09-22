import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { draftAskProse, explicitSummarySource } from "./summarize.js";

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

describe("short summary", () => {
  it("reads only an explicit summarize command", () => {
    expect(explicitSummarySource("summarize: The filters ship tomorrow.")).toBe(
      "The filters ship tomorrow.",
    );
    expect(explicitSummarySource("What should I do next?")).toBeNull();
    expect(explicitSummarySource("summarize:")).toBeNull();
  });

  it("repairs an empty summary once", async () => {
    const model = scripted(["", "The filters ship tomorrow."]);
    const result = await draftAskProse({
      model,
      ask: "summarize: We promised the filters would ship tomorrow after the review.",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(result).toEqual({ ok: true, text: "The filters ship tomorrow.", attempts: 2 });
  });

  it("does not publish a summary that repeats the instructions", async () => {
    const model = scripted([
      "Summarize the following text in one or two short sentences.",
      "Do not add facts that are not present.",
    ]);
    const result = await draftAskProse({
      model,
      ask: "summarize: The report is ready.",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(result).toEqual({ ok: false, reason: "invalid_response", attempts: 2 });
  });

  it("leaves an ordinary ask on the direct-answer path", async () => {
    const model = scripted(["Use the release checklist."]);
    const result = await draftAskProse({
      model,
      ask: "What should I do next?",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(1);
    expect(result.ok).toBe(true);
  });
});
