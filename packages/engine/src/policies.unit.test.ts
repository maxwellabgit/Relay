import { describe, expect, it } from "vitest";
import { shouldCreateCaseForFinal } from "./policies.js";

describe("case routing policy", () => {
  it("gives direct Ask a higher priority lane without inventing a listen super-case", () => {
    expect(shouldCreateCaseForFinal("typed", true)).toEqual({
      origin: "direct",
      kind: "answer",
      priority: 100,
    });
    expect(shouldCreateCaseForFinal("scripted_transcript", false)).toEqual({
      origin: "observed",
      kind: "resolve",
      priority: 50,
    });
  });
});
