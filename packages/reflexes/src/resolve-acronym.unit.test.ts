import { describe, expect, it } from "vitest";
import { applyAcronymPolicy, detectAcronymTokens } from "./index.js";

describe("resolve-acronym", () => {
  it("detects deterministic uppercase tokens", () => {
    expect(detectAcronymTokens("Check the API and BESS today").map((t) => t.token)).toEqual([
      "API",
      "BESS",
    ]);
  });

  it("applies versioned display policy over recorded probabilities", () => {
    const decision = applyAcronymPolicy(
      {
        expansion: {
          type: "choice",
          choice: "Application Programming Interface",
          probabilities: {
            "Application Programming Interface": 0.8,
            no_match: 0.2,
          },
          confidence: 0.8,
        },
        useful: { type: "noul", probabilityYes: 0.75 },
      },
      { isExplicitAsk: false },
    );
    expect(decision.show).toBe(true);
    expect(decision.reasonCode).toBe("policy_pass");
  });

  it("lets explicit Ask bypass usefulness but not no_match", () => {
    const noMatch = applyAcronymPolicy(
      {
        expansion: {
          type: "choice",
          choice: "no_match",
          probabilities: { no_match: 0.9 },
          confidence: 0.9,
        },
        useful: { type: "noul", probabilityYes: 0.1 },
      },
      { isExplicitAsk: true },
    );
    expect(noMatch.show).toBe(false);
    expect(noMatch.reasonCode).toBe("no_match");
  });
});
