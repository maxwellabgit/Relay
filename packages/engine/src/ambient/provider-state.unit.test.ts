import { describe, expect, it } from "vitest";
import { JEV_MODEL, wireTypeSafeBody } from "../typesafe-judgment.js";
import { ambientProviderState } from "./provider-state.js";

describe("ambient provider state", () => {
  it("sends the authorized excerpt and excludes neighboring content", () => {
    const authorized = "We promised to send the release report tomorrow.";
    const neighbor = "Unrelated payroll argument that must stay local.";
    const state = ambientProviderState(authorized);
    const body = wireTypeSafeBody({
      model: "typesafe",
      state,
      questions: {
        possible_commitment: {
          type: "noul",
          instructions: "Is this a commitment?",
        },
      },
    });
    expect(body.model).toBe(JEV_MODEL);
    expect(JSON.stringify(body)).toContain(authorized);
    expect(JSON.stringify(body)).not.toContain(neighbor);
    expect(body.state).toEqual({ origin: "observed", excerpt: authorized });
  });
});
