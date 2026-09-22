import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { draftSearchQuery } from "./search-query.js";

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

describe("search query draft", () => {
  it("repairs one invalid query and then stops", async () => {
    const model = scripted(["this is a sentence\nand another", "release report"]);
    const query = await draftSearchQuery({
      model,
      ask: "Find the release report",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(query).toBe("release report");
  });

  it("falls back to the Ask when both drafts are invalid", async () => {
    const model = scripted(["\n", "still\nbad"]);
    const query = await draftSearchQuery({
      model,
      ask: "Find the release report",
      signal: new AbortController().signal,
    });
    expect(model.calls()).toBe(2);
    expect(query).toBe("Find the release report");
  });
});
