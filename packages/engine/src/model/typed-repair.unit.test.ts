import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { extractCandidate } from "../intake/CandidateExtractor.js";
import { LOCAL_TYPED_CORPUS } from "./typed-corpus.js";
import { MAX_TYPED_REPAIRS, generateTyped } from "./typed-repair.js";

const slice = {
  artifactId: "artifact_corpus",
  sha256: "abc",
  start: 0,
  end: 8,
  offsetsValidated: true,
};

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

describe("typed local draft repair", () => {
  it("repairs each corpus row once and stops", async () => {
    expect(MAX_TYPED_REPAIRS).toBe(1);
    for (const row of LOCAL_TYPED_CORPUS) {
      const model = scripted([row.invalid, row.valid]);
      const extracted = await extractCandidate({
        text: row.text,
        isAsk: false,
        sourceSlice: slice,
        model,
      });
      expect(model.calls(), row.id).toBe(2);
      expect(extracted.kind, row.id).toBe(row.kind);
      expect(extracted.invalid, row.id).toBe(false);
    }
  });

  it("accepts a valid first draft without a second call", async () => {
    const model = scripted(["kind: commitment", "kind: none"]);
    const extracted = await extractCandidate({
      text: "We will send the release report tomorrow.",
      isAsk: false,
      sourceSlice: slice,
      model,
    });
    expect(model.calls()).toBe(1);
    expect(extracted.kind).toBe("commitment");
  });

  it("stays invalid when the repair is also invalid", async () => {
    const model = scripted(["kind: banana", "still wrong"]);
    const extracted = await extractCandidate({
      text: "We will send the release report tomorrow.",
      isAsk: false,
      sourceSlice: slice,
      model,
    });
    expect(model.calls()).toBe(2);
    expect(extracted.invalid).toBe(true);
    expect(extracted.kind).toBe("none");
  });

  it("does not repair after cancellation", async () => {
    const signal = new AbortController();
    signal.abort();
    const model = scripted(["kind: banana", "kind: commitment"]);
    const result = await generateTyped({
      model,
      signal: signal.signal,
      request: { taskKind: "extract_candidate", prompt: "Text" },
      accept: () => false,
      repairPrompt: () => "repair",
    });
    expect(result).toEqual({ ok: false, reason: "cancelled", attempts: 0 });
    expect(model.calls()).toBe(0);
  });
});
