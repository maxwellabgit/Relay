import { describe, expect, it } from "vitest";
import {
  calendarRecommendation,
  CALENDAR_SIGNATURE,
  foldPattern,
  patternReady,
  reviewTrigger,
  workSignature,
} from "./learning-store.js";
import { evaluateChoiceGate } from "./policies.js";

describe("bounded expansion rules", () => {
  it("requires equivalent signatures, not a shared bucket of unknowns", () => {
    expect(workSignature("acronym.lookup", { token: "MSRP", outcome: "no_candidates" })).not.toBe(
      workSignature("acronym.lookup", { token: "BESS", outcome: "no_candidates" }),
    );
    expect(workSignature("calendar.block", { start_bucket: "noon", duration: "60m", reminder_offset: "-1d" })).toBe(
      CALENDAR_SIGNATURE,
    );
  });

  it("treats 2 episodes, 3 episodes in one session, and 3 across sessions as different gates", () => {
    const one = foldPattern(null, episode("e1", "s1"));
    expect(patternReady(one)).toBe(false);
    const two = foldPattern(one, episode("e2", "s1"));
    expect(patternReady(two)).toBe(false);
    const threeSame = foldPattern(two, episode("e3", "s1"));
    expect(threeSame.count).toBe(3);
    expect(patternReady(threeSame)).toBe(false);
    const threeSessions = foldPattern(foldPattern(one, episode("e2", "s2")), episode("e3", "s3"));
    expect(patternReady(threeSessions)).toBe(true);
    expect(calendarRecommendation(threeSessions)).toBe(
      "Observed 3 completed calendar-block episodes across 3 sessions: 60 minutes near noon, reminder one day before. Recommend creating a Calendar Block Reflex?",
    );
  });

  it("fires each review rule on its own threshold", () => {
    expect(reviewTrigger({ completeSessions: 11, approvedReflexes: 0, completeEpisodes: 0, qualifiedCandidates: 0 })).toBeNull();
    expect(reviewTrigger({ completeSessions: 12, approvedReflexes: 0, completeEpisodes: 0, qualifiedCandidates: 0 })).toBe("sessions");
    expect(reviewTrigger({ completeSessions: 0, approvedReflexes: 4, completeEpisodes: 0, qualifiedCandidates: 0 })).toBe("reflexes");
    expect(reviewTrigger({ completeSessions: 0, approvedReflexes: 0, completeEpisodes: 25, qualifiedCandidates: 0 })).toBe("episodes");
    expect(reviewTrigger({ completeSessions: 0, approvedReflexes: 0, completeEpisodes: 0, qualifiedCandidates: 3 })).toBe("candidates");
  });

  it("fails choice just below the minimum and passes at the minimum", () => {
    const below = evaluateChoiceGate({
      probabilities: { Battery: 0.64, Bessemer: 0.36 },
      minimum: 0.65,
      marginMinimum: 0.15,
    });
    const equal = evaluateChoiceGate({
      probabilities: { Battery: 0.65, Bessemer: 0.2 },
      minimum: 0.65,
      marginMinimum: 0.15,
    });
    const above = evaluateChoiceGate({
      probabilities: { Battery: 0.66, Bessemer: 0.2 },
      minimum: 0.65,
      marginMinimum: 0.15,
    });
    expect(below.pass).toBe(false);
    expect(below.reasonCode).toBe("below_choice_minimum");
    expect(equal.pass).toBe(true);
    expect(above.pass).toBe(true);
  });
});

function episode(episodeId: string, sessionId: string) {
  return {
    episodeId,
    sessionId,
    caseId: null,
    signature: CALENDAR_SIGNATURE,
    outcome: "completed" as const,
    startedAt: "2026-09-19T00:00:00.000Z",
    completedAt: "2026-09-19T00:00:00.000Z",
  };
}
