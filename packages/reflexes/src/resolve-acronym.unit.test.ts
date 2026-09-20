import { describe, expect, it } from "vitest";
import { applyAcronymPolicy, bundledDictionaryLookup, createResolveAcronymModule, detectAcronymTokens } from "./index.js";
import { createFixtureGlossaryLookup } from "./resolve-acronym/glossary-lookup.js";

describe("resolve-acronym", () => {
  it("detects deterministic uppercase tokens", () => {
    expect(detectAcronymTokens("Check the API and BESS today").map((t) => t.token)).toEqual([
      "API",
      "BESS",
    ]);
  });

  it("resolves bundled dictionary hits without Jev", async () => {
    const module = createResolveAcronymModule();
    const result = await module.evaluate({
      caseId: "c1",
      caseVersion: 1,
      reflex: { id: "resolve-acronym", version: 1 },
      triggerSourceRefs: [],
      eligibleConnections: [],
      remainingBudgets: { maxSourceAttempts: 1, maxJudgmentRounds: 1, maxHostedTokens: 0 },
      now: new Date().toISOString(),
      observationText: "What does API mean?",
      triggerToken: "API",
      isExplicitAsk: true,
    });
    expect(result.type).toBe("finding");
    expect(result.summary).toContain("Application Programming Interface");
    expect(bundledDictionaryLookup("API")).toBe("Application Programming Interface");
  });

  it("prefers explicit user memory over bundled dictionary", async () => {
    const module = createResolveAcronymModule({
      glossary: {
        exactUser: async () => "Always Prefer Intent",
        exactProject: async () => null,
        exactBundled: async () => "Application Programming Interface",
        searchWindow: async () => [],
      },
    });
    const result = await module.evaluate({
      caseId: "c1",
      caseVersion: 1,
      reflex: { id: "resolve-acronym", version: 1 },
      triggerSourceRefs: [],
      eligibleConnections: [],
      remainingBudgets: { maxSourceAttempts: 1, maxJudgmentRounds: 1, maxHostedTokens: 0 },
      now: new Date().toISOString(),
      observationText: "API",
      triggerToken: "API",
      isExplicitAsk: true,
    });
    expect(result.summary).toContain("Always Prefer Intent");
  });

  it("requests Jev for multiple context candidates", async () => {
    const module = createResolveAcronymModule({
      glossary: createFixtureGlossaryLookup({
        ABC: ["Alpha Beta Corp", "Another Big Choice"],
      }),
    });
    const result = await module.evaluate({
      caseId: "c1",
      caseVersion: 1,
      reflex: { id: "resolve-acronym", version: 1 },
      triggerSourceRefs: [],
      eligibleConnections: [],
      remainingBudgets: { maxSourceAttempts: 1, maxJudgmentRounds: 1, maxHostedTokens: 0 },
      now: new Date().toISOString(),
      observationText: "ABC",
      triggerToken: "ABC",
      isExplicitAsk: true,
    });
    expect(result.type).toBe("clarification_required");
  });

  it("does not invent unknown acronyms", async () => {
    const module = createResolveAcronymModule({
      glossary: createFixtureGlossaryLookup({}),
    });
    const result = await module.evaluate({
      caseId: "c1",
      caseVersion: 1,
      reflex: { id: "resolve-acronym", version: 1 },
      triggerSourceRefs: [],
      eligibleConnections: [],
      remainingBudgets: { maxSourceAttempts: 1, maxJudgmentRounds: 1, maxHostedTokens: 0 },
      now: new Date().toISOString(),
      observationText: "ZXQPV",
      triggerToken: "ZXQPV",
      isExplicitAsk: true,
    });
    expect(result.type).toBe("no_action");
    expect(result.summary).toBe("no_candidates");
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
