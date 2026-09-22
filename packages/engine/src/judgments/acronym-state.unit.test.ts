import { describe, expect, it } from "vitest";
import { acronymProviderState } from "./acronym-state.js";

describe("acronym provider state", () => {
  it("includes the authorized utterance and not a neighboring sentence", () => {
    const utterance = "In this pharmaceutical discussion, API stands for Active Pharmaceutical Ingredient.";
    const neighbor = "The warehouse door code is 4419.";
    const state = acronymProviderState({
      token: "API",
      contextExcerpt: utterance,
      optionCount: 2,
      explicitAsk: true,
      sourceEventId: "src_1",
    });
    const encoded = JSON.stringify(state);
    expect(encoded).toContain(utterance);
    expect(encoded).toContain("API");
    expect(encoded).not.toContain(neighbor);
  });
});