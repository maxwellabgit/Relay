import { describe, expect, it } from "vitest";
import type { ReceiptRecord } from "./learning-store.js";
import { projectCaseExecution, selectCaseIdForExecution } from "./project-case-execution.js";
import type { RuntimeEventV2 } from "./runtime-events.js";

function event(
  partial: Partial<RuntimeEventV2> & Pick<RuntimeEventV2, "sequence" | "eventType" | "stage" | "status">,
): RuntimeEventV2 {
  return {
    schemaVersion: 2,
    runId: "run_test",
    at: partial.at ?? `2024-01-01T00:00:00.${String(partial.sequence).padStart(3, "0")}Z`,
    caseId: partial.caseId ?? "case_1",
    ...partial,
  };
}

function receipt(partial: Partial<ReceiptRecord> = {}): ReceiptRecord {
  return {
    receiptId: partial.receiptId ?? "receipt_1",
    decisionId: partial.decisionId ?? "receipt_1",
    caseId: partial.caseId ?? "case_1",
    judgmentId: partial.judgmentId ?? null,
    reflexId: partial.reflexId ?? "resolve-acronym",
    gateId: partial.gateId ?? "resolve-acronym",
    policyVersion: partial.policyVersion ?? "resolve-acronym@1",
    questionType: partial.questionType ?? "not_applicable",
    provider: partial.provider ?? "not_applicable",
    probabilities: partial.probabilities ?? {},
    thresholds: partial.thresholds ?? {},
    optionLabels: partial.optionLabels ?? {},
    selectedOption: partial.selectedOption ?? null,
    selectedOptionId: partial.selectedOptionId ?? null,
    result: partial.result ?? "not_applicable",
    reasonCode: partial.reasonCode ?? "exact_glossary",
    latencyMs: partial.latencyMs ?? null,
    retries: partial.retries ?? 0,
    requestedAt: partial.requestedAt ?? null,
    completedAt: partial.completedAt ?? null,
    createdAt: partial.createdAt ?? "2024-01-01T00:00:00.010Z",
  };
}

describe("projectCaseExecution", () => {
  it("returns null without a case id or events", () => {
    expect(
      projectCaseExecution({
        caseId: null,
        events: [],
        receipt: null,
        caseStatus: null,
        historical: false,
      }),
    ).toBeNull();
    expect(
      projectCaseExecution({
        caseId: "case_1",
        events: [],
        receipt: null,
        caseStatus: null,
        historical: false,
      }),
    ).toBeNull();
  });

  it("projects local-model Ask timing without inventing skipped model", () => {
    const view = projectCaseExecution({
      caseId: "case_1",
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          at: "2024-01-01T00:00:00.000Z",
        }),
        event({
          sequence: 2,
          eventType: "case.created",
          stage: "case.create",
          status: "completed",
          at: "2024-01-01T00:00:00.003Z",
        }),
        event({
          sequence: 3,
          eventType: "model.requested",
          stage: "model.request",
          status: "started",
          at: "2024-01-01T00:00:00.004Z",
        }),
        event({
          sequence: 4,
          eventType: "model.completed",
          stage: "model.response",
          status: "completed",
          at: "2024-01-01T00:00:00.828Z",
          durationMs: 824,
        }),
        event({
          sequence: 5,
          eventType: "answer.committed",
          stage: "episode.complete",
          status: "completed",
          at: "2024-01-01T00:00:00.832Z",
        }),
      ],
      receipt: null,
      caseStatus: "completed",
      historical: false,
    });

    expect(view).not.toBeNull();
    expect(view!.totalMs).toBe(832);
    expect(view!.outcome).toBe("answered");
    expect(view!.steps.map((step) => `${step.label}:${step.state}:${step.deltaMs}:${step.durationMs}`)).toEqual([
      "Input accepted:passed:0:null",
      "Case created:passed:3:null",
      "Jev:skipped:null:null",
      "Local model:passed:825:824",
      "Answer committed:passed:4:null",
    ]);
    expect(view!.steps.some((step) => step.label === "Local model" && step.state === "skipped")).toBe(false);
  });

  it("projects acronym/Jev stages without inventing local model", () => {
    const view = projectCaseExecution({
      caseId: "case_1",
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          at: "2024-01-01T00:00:00.000Z",
        }),
        event({
          sequence: 2,
          eventType: "case.created",
          stage: "case.create",
          status: "completed",
          at: "2024-01-01T00:00:00.002Z",
        }),
        event({
          sequence: 3,
          eventType: "reflex.detected",
          stage: "reflex.detect",
          status: "completed",
          at: "2024-01-01T00:00:00.003Z",
          reflexId: "resolve-acronym",
        }),
        event({
          sequence: 4,
          eventType: "judgment.requested",
          stage: "judgment.request",
          status: "started",
          at: "2024-01-01T00:00:00.010Z",
        }),
        event({
          sequence: 5,
          eventType: "judgment.completed",
          stage: "judgment.response",
          status: "completed",
          at: "2024-01-01T00:00:00.210Z",
          durationMs: 200,
        }),
        event({
          sequence: 6,
          eventType: "policy.evaluated",
          stage: "policy.evaluate",
          status: "completed",
          at: "2024-01-01T00:00:00.212Z",
          reasonCode: "policy_pass",
        }),
        event({
          sequence: 7,
          eventType: "answer.committed",
          stage: "episode.complete",
          status: "completed",
          at: "2024-01-01T00:00:00.215Z",
        }),
      ],
      receipt: receipt({
        questionType: "choice",
        provider: "typesafe",
        result: "pass",
        reasonCode: "policy_pass",
      }),
      caseStatus: "completed",
      historical: false,
    });

    expect(view!.totalMs).toBe(215);
    expect(view!.steps.map((step) => step.label)).toEqual([
      "Input accepted",
      "Case created",
      "Acronym detection",
      "Jev request",
      "Jev response",
      "Policy gate",
      "Answer committed",
    ]);
    expect(view!.steps.some((step) => step.key === "model")).toBe(false);
  });

  it("reports resolved without answer instead of inventing duration", () => {
    const view = projectCaseExecution({
      caseId: "case_1",
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          at: "2024-01-01T00:00:00.000Z",
        }),
        event({
          sequence: 2,
          eventType: "outcome.recorded",
          stage: "episode.complete",
          status: "completed",
          at: "2024-01-01T00:00:00.050Z",
        }),
      ],
      receipt: null,
      caseStatus: "completed",
      historical: false,
    });
    expect(view!.totalMs).toBeNull();
    expect(view!.outcome).toBe("resolved_without_answer");
  });

  it("reports failed and blocked outcomes", () => {
    const failed = projectCaseExecution({
      caseId: "case_1",
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
        }),
        event({
          sequence: 2,
          eventType: "judgment.failed",
          stage: "judgment.response",
          status: "failed",
        }),
      ],
      receipt: receipt({ result: "fail", questionType: "choice", provider: "typesafe" }),
      caseStatus: "failed",
      historical: false,
    });
    expect(failed!.outcome).toBe("failed");
    expect(failed!.totalMs).toBeNull();

    const blocked = projectCaseExecution({
      caseId: "case_1",
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
        }),
      ],
      receipt: receipt({
        result: "blocked",
        reasonCode: "hosted_processing_disabled",
        questionType: "choice",
        provider: "typesafe",
      }),
      caseStatus: "blocked",
      historical: false,
    });
    expect(blocked!.outcome).toBe("blocked");
  });

  it("selects active case when present", () => {
    const events = [
      event({ sequence: 1, eventType: "source.accepted", stage: "source.accept", status: "completed", caseId: "case_old" }),
      event({ sequence: 2, eventType: "source.accepted", stage: "source.accept", status: "completed", caseId: "case_new" }),
    ];
    expect(selectCaseIdForExecution(events, "case_old")).toEqual({ caseId: "case_old", historical: false });
    expect(selectCaseIdForExecution(events, null).caseId).toBe("case_new");
  });
});
