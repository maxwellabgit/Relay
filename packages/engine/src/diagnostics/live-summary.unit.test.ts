import { describe, expect, it } from "vitest";
import { observeDiagnostics } from "./live-summary.js";

describe("observeDiagnostics", () => {
  it("keeps unknowns unobserved until structural events arrive", () => {
    const observed = observeDiagnostics(
      [
        {
          at: "2026-09-22T12:00:00.000Z",
          eventType: "source.accepted",
          status: "completed",
        },
      ],
      Date.parse("2026-09-22T12:00:01.000Z"),
    );
    expect(observed.latestJudgment.observed).toBe(false);
    expect(observed.latestTool.observed).toBe(false);
    expect(observed.oldestReadyMs).toBe(0);
    expect(observed.retrySchedule).toBeNull();
  });

  it("derives queue age, tool result, and retry time from events", () => {
    const now = Date.parse("2026-09-22T12:00:05.000Z");
    const observed = observeDiagnostics(
      [
        {
          at: "2026-09-22T12:00:00.000Z",
          eventType: "case.created",
          status: "completed",
          caseId: "case_1",
          stage: "case.create",
          queueDepth: 1,
        },
        {
          at: "2026-09-22T12:00:01.000Z",
          eventType: "tool.completed",
          status: "completed",
          caseId: "case_1",
          stage: "tool.execute",
          toolId: "assistant.respond",
          reasonCode: "completed",
          queueDepth: 1,
        },
        {
          at: "2026-09-22T12:00:02.000Z",
          eventType: "judgment.failed",
          status: "waiting",
          caseId: "case_1",
          stage: "judgment.response",
          reasonCode: "timeout",
          attempt: 1,
          reflexId: "reflex.resolve-acronym",
          queueDepth: 1,
        },
      ],
      now,
    );
    expect(observed.queueReady).toBe(1);
    expect(observed.oldestReadyMs).toBe(5_000);
    expect(observed.latestTool).toEqual({
      toolId: "assistant.respond",
      result: "completed",
      observed: true,
    });
    expect(observed.retrySchedule).toBe("2026-09-22T12:00:02.200Z");
    expect(observed.latestJudgment.observed).toBe(true);
    expect(observed.latestJudgment.questionSet).toBe("reflex.resolve-acronym");
    expect(observed.waitingCases).toBe(1);
    expect(observed.latestCase.blocker).toBe("timeout");
  });
});
