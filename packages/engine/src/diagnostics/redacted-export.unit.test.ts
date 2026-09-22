import { describe, expect, it } from "vitest";
import type { RelaySnapshot } from "@relay/contracts";
import { buildRedactedDiagnostics } from "./redacted-export.js";

describe("redacted diagnostics export", () => {
  it("keeps the run summary and drops source transcript text", () => {
    const snapshot = {
      runtime: { runId: "run_1", commit: "abc", mode: "live", deadLetters: 1 },
      status: [{ id: "audio", label: "Audio", ok: false, detail: "speech_unavailable" }],
      queueDepth: 2,
      cases: [],
      waits: [],
      decision: null,
      gate: null,
      trace: [],
      feedItems: [{ itemId: "feed_1", kind: "answer", summary: "Saved note: filters" }],
      sourceSegments: [{ text: "SECRET_TRANSCRIPT" }],
      memories: [{ fields: { text: "SECRET_MEMORY" } }],
    } as unknown as RelaySnapshot;
    const json = JSON.stringify(buildRedactedDiagnostics(snapshot));
    expect(json).toContain("run_1");
    expect(json).toContain("speech_unavailable");
    expect(json).toContain("Saved note: filters");
    expect(json).not.toContain("SECRET_TRANSCRIPT");
    expect(json).not.toContain("SECRET_MEMORY");
  });
});