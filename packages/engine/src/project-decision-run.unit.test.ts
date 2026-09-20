import { describe, expect, it } from "vitest";
import { loopTest } from "@relay/testkit";
import type { ReceiptRecord } from "./learning-store.js";
import { projectDecisionRun, selectReceiptForDecision } from "./project-decision-run.js";
import type { RuntimeEventV2 } from "./runtime-events.js";

function receipt(partial: Partial<ReceiptRecord> & Pick<ReceiptRecord, "receiptId" | "caseId">): ReceiptRecord {
  return {
    decisionId: partial.decisionId ?? partial.receiptId,
    judgmentId: partial.judgmentId ?? null,
    reflexId: partial.reflexId ?? "resolve-acronym",
    gateId: partial.gateId ?? "resolve-acronym",
    policyVersion: partial.policyVersion ?? "resolve-acronym@1",
    questionType: partial.questionType ?? "choice",
    provider: partial.provider ?? "typesafe",
    probabilities: partial.probabilities ?? {},
    thresholds: partial.thresholds ?? {},
    optionLabels: partial.optionLabels ?? {},
    selectedOption: partial.selectedOption ?? null,
    selectedOptionId: partial.selectedOptionId ?? partial.selectedOption ?? null,
    result: partial.result ?? "pass",
    reasonCode: partial.reasonCode ?? "policy_pass",
    latencyMs: partial.latencyMs ?? null,
    retries: partial.retries ?? 0,
    requestedAt: partial.requestedAt ?? "2024-01-01T00:00:00.000Z",
    completedAt: partial.completedAt ?? "2024-01-01T00:00:00.100Z",
    createdAt: partial.createdAt ?? "2024-01-01T00:00:00.100Z",
    receiptId: partial.receiptId,
    caseId: partial.caseId,
  };
}

function event(
  partial: Partial<RuntimeEventV2> & Pick<RuntimeEventV2, "sequence" | "eventType" | "stage" | "status">,
): RuntimeEventV2 {
  return {
    schemaVersion: 2,
    runId: "run_test",
    at: partial.at ?? `2024-01-01T00:00:00.${String(partial.sequence).padStart(3, "0")}Z`,
    ...partial,
  };
}

describe("projectDecisionRun", () => {
  it("returns null for empty receipt", () => {
    expect(
      projectDecisionRun({ receipt: null, events: [], activeCaseId: null, historical: false }),
    ).toBeNull();
  });

  it("rejects receipts without caseId", async () => {
    await loopTest("reject-uncorrelated", ({ seed }) => {
      const view = projectDecisionRun({
        receipt: receipt({ receiptId: `receipt_${seed}`, caseId: null }),
        events: [
          event({
            sequence: 1,
            eventType: "source.accepted",
            stage: "source.accept",
            status: "completed",
            caseId: "case_a",
          }),
        ],
        activeCaseId: null,
        historical: false,
      });
      expect(view).toBeNull();
    });
  });

  it("marks no stages taken when events are empty for a correlated receipt", () => {
    const view = projectDecisionRun({
      receipt: receipt({
        receiptId: "receipt_1",
        caseId: "case_1",
        questionType: "choice",
        result: "pass",
      }),
      events: [],
      activeCaseId: "case_1",
      historical: false,
    });
    expect(view).not.toBeNull();
    expect(view!.stages.every((stage) => stage.state !== "passed" || stage.stage === "policy.evaluate")).toBe(true);
    const taken = view!.stages.filter((stage) => stage.state === "passed" || stage.state === "running");
    // policy.evaluate may appear from receipt alone for choice; no invented source/intent
    expect(view!.stages.some((stage) => stage.stage === "source.accept")).toBe(false);
    expect(view!.stages.some((stage) => stage.stage === "reflex.detect")).toBe(false);
    expect(taken.every((stage) => stage.stage === "policy.evaluate")).toBe(true);
  });

  it("skips Jev for local deterministic glossary hits", () => {
    const view = projectDecisionRun({
      receipt: receipt({
        receiptId: "receipt_local",
        caseId: "case_local",
        questionType: "not_applicable",
        provider: "not_applicable",
        reasonCode: "exact_glossary",
        result: "not_applicable",
      }),
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          caseId: "case_local",
        }),
        event({
          sequence: 2,
          eventType: "reflex.detected",
          stage: "reflex.detect",
          status: "completed",
          caseId: "case_local",
        }),
        event({
          sequence: 3,
          eventType: "policy.evaluated",
          stage: "policy.evaluate",
          status: "completed",
          caseId: "case_local",
          reasonCode: "exact_glossary",
        }),
      ],
      activeCaseId: "case_local",
      historical: false,
    });
    expect(view!.stages.find((stage) => stage.stage === "judgment.request")?.state).toBe("skipped");
    expect(view!.stages.find((stage) => stage.stage === "judgment.response")?.state).toBe("skipped");
    expect(view!.elapsedMs).toBe(100);
  });

  it("reports elapsedMs from requested/completed only", () => {
    const view = projectDecisionRun({
      receipt: receipt({
        receiptId: "receipt_latency",
        caseId: "case_latency",
        requestedAt: "2024-01-01T00:00:00.000Z",
        completedAt: "2024-01-01T00:00:00.100Z",
        latencyMs: 100,
      }),
      events: [
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          caseId: "case_latency",
          durationMs: 900,
        }),
        event({
          sequence: 2,
          eventType: "judgment.completed",
          stage: "judgment.response",
          status: "completed",
          caseId: "case_latency",
          durationMs: 1000,
        }),
      ],
      activeCaseId: "case_latency",
      historical: false,
    });
    expect(view!.elapsedMs).toBe(100);
  });

  it("never mixes events across two cases", () => {
    const view = projectDecisionRun({
      receipt: receipt({ receiptId: "receipt_a", caseId: "case_a", decisionId: "decision_a" }),
      events: [
        event({
          sequence: 2,
          eventType: "judgment.completed",
          stage: "judgment.response",
          status: "completed",
          caseId: "case_b",
          durationMs: 50,
        }),
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          caseId: "case_a",
        }),
      ],
      activeCaseId: "case_a",
      historical: false,
    });
    expect(view!.stages.some((stage) => stage.stage === "judgment.response")).toBe(false);
    expect(view!.stages.find((stage) => stage.stage === "source.accept")?.state).toBe("passed");
  });

  it("sorts out-of-order events by sequence", () => {
    const view = projectDecisionRun({
      receipt: receipt({ receiptId: "receipt_order", caseId: "case_order" }),
      events: [
        event({
          sequence: 3,
          eventType: "policy.evaluated",
          stage: "policy.evaluate",
          status: "completed",
          caseId: "case_order",
          reasonCode: "policy_pass",
        }),
        event({
          sequence: 1,
          eventType: "source.accepted",
          stage: "source.accept",
          status: "completed",
          caseId: "case_order",
        }),
        event({
          sequence: 2,
          eventType: "judgment.requested",
          stage: "judgment.request",
          status: "completed",
          caseId: "case_order",
        }),
      ],
      activeCaseId: "case_order",
      historical: false,
    });
    expect(view!.stages.map((stage) => stage.stage)).toEqual([
      "source.accept",
      "judgment.request",
      "policy.evaluate",
    ]);
  });

  it("keeps multiple attempts under one decision", () => {
    const view = projectDecisionRun({
      receipt: receipt({ receiptId: "receipt_retry", caseId: "case_retry", retries: 2 }),
      events: [
        event({
          sequence: 1,
          eventType: "judgment.failed",
          stage: "judgment.response",
          status: "waiting",
          caseId: "case_retry",
          attempt: 1,
          reasonCode: "rate_limited",
          durationMs: 10,
        }),
        event({
          sequence: 2,
          eventType: "judgment.failed",
          stage: "judgment.response",
          status: "waiting",
          caseId: "case_retry",
          attempt: 2,
          reasonCode: "rate_limited",
          durationMs: 12,
        }),
        event({
          sequence: 3,
          eventType: "judgment.completed",
          stage: "judgment.response",
          status: "completed",
          caseId: "case_retry",
          attempt: 3,
          durationMs: 20,
        }),
      ],
      activeCaseId: "case_retry",
      historical: false,
    });
    expect(view!.attempts).toHaveLength(3);
    expect(view!.attempts.map((item) => item.attempt)).toEqual([1, 2, 3]);
  });

  it("selects active case receipt over global latest", () => {
    const selected = selectReceiptForDecision(
      [
        receipt({ receiptId: "receipt_old", caseId: "case_old", createdAt: "2024-01-01T00:00:01.000Z" }),
        receipt({ receiptId: "receipt_new", caseId: "case_new", createdAt: "2024-01-01T00:00:02.000Z" }),
      ],
      "case_old",
    );
    expect(selected.receipt?.receiptId).toBe("receipt_old");
    expect(selected.historical).toBe(false);
  });
});
