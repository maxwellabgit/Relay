import { describe, expect, it } from "vitest";
import type { AmbientTriageScores } from "@relay/contracts";
import { decideAmbientRoute } from "./route-policy.js";

function scores(overrides: Partial<AmbientTriageScores> = {}): AmbientTriageScores {
  return {
    worth_remembering: 0.1,
    possible_fact_claim: 0.1,
    possible_correction: 0.1,
    possible_commitment: 0.1,
    possible_open_question: 0.1,
    related_to_active_case: 0.1,
    interrupt_worthy: 0.1,
    ...overrides,
  };
}

describe("decideAmbientRoute", () => {
  it("ignores ordinary chatter below threshold", () => {
    const decision = decideAmbientRoute({
      scores: scores(),
      kind: "none",
    });
    expect(decision.route).toBe("ignore");
    expect(decision.quiet).toBe(true);
  });

  it("proposes a quiet note for durable information", () => {
    const decision = decideAmbientRoute({
      scores: scores({ worth_remembering: 0.85 }),
      kind: "durable_information",
    });
    expect(decision.route).toBe("propose_note");
    expect(decision.reasonCode).toBe("worth_remembering");
    expect(decision.quiet).toBe(true);
  });

  it("routes corrections toward a note when worth remembering clears the quiet bar", () => {
    const decision = decideAmbientRoute({
      scores: scores({ possible_correction: 0.82, worth_remembering: 0.65 }),
      kind: "correction",
    });
    expect(decision.route).toBe("propose_note");
    expect(decision.reasonCode).toBe("correction");
  });

  it("suggests a task for commitments", () => {
    const decision = decideAmbientRoute({
      scores: scores({ possible_commitment: 0.82 }),
      kind: "commitment",
    });
    expect(decision.route).toBe("suggest_next_task");
    expect(decision.reasonCode).toBe("commitment");
  });

  it("interrupts only with high interrupt score and supported urgency", () => {
    const blocked = decideAmbientRoute({
      scores: scores({ interrupt_worthy: 0.95 }),
      kind: "commitment",
      urgencyReason: null,
    });
    expect(blocked.route).toBe("ignore");

    const urgent = decideAmbientRoute({
      scores: scores({ interrupt_worthy: 0.95, possible_commitment: 0.5 }),
      kind: "commitment",
      urgencyReason: "blocking_decision",
    });
    expect(urgent.route).toBe("interrupt");
    expect(urgent.reasonCode).toBe("urgency:blocking_decision");
    expect(urgent.quiet).toBe(false);
  });

  it("respects suppression keys", () => {
    const decision = decideAmbientRoute({
      scores: scores({ worth_remembering: 0.9 }),
      kind: "durable_information",
      suppressed: true,
    });
    expect(decision.route).toBe("ignore");
    expect(decision.reasonCode).toBe("suppressed");
  });
});
