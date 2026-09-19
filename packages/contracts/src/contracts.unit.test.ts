import { describe, expect, it } from "vitest";
import {
  combinePolicies,
  DataSensitivityFlags,
  isHostedEligible,
  localOnlyPolicy,
  observedPolicy,
  publicPolicy,
} from "./artifacts.js";
import {
  CHOICE_NO_MATCH,
  isRetryableJudgmentFailure,
  validateAnswerProbability,
  validateQuestion,
} from "./judgments.js";
import {
  scopeStillGranted,
  tryApprove,
  tryMutateConnection,
  tryMutateReflex,
  tryReject,
  type PendingOperationApproval,
} from "./operations.js";

function samplePending(
  overrides: Partial<PendingOperationApproval> = {},
): PendingOperationApproval {
  return {
    operationId: "op1",
    action: {
      connectorId: "memory",
      connectorVersion: 1,
      actionId: "acronym-remember",
      actionVersion: 1,
    },
    canonicalHash: "abc",
    caseVersion: 3,
    connectionId: "c1",
    connectionVersion: 2,
    grantedScopeKeys: new Set(["memory.write"]),
    writeActionEnabled: true,
    boundActionVersion: 1,
    ...overrides,
  };
}

describe("data policy", () => {
  it("combines disclosure to the strictest class and ORs sensitivity", () => {
    const combined = combinePolicies(
      observedPolicy(DataSensitivityFlags.Email),
      publicPolicy(),
    );
    expect(combined.disclosure).toBe("local_only");
    expect(combined.sensitivity).toBe(DataSensitivityFlags.Email);
  });

  it("marks only hosted-eligible disclosures", () => {
    expect(isHostedEligible(localOnlyPolicy())).toBe(false);
    expect(isHostedEligible(publicPolicy())).toBe(true);
  });
});

describe("judgment contracts", () => {
  it("requires no_match on choice questions by default", () => {
    expect(
      validateQuestion({
        type: "choice",
        instructions: "pick",
        criteria: { a: "A" },
      }),
    ).toBe("missing_no_match");
    expect(
      validateQuestion({
        type: "choice",
        instructions: "pick",
        criteria: { a: "A", [CHOICE_NO_MATCH]: "none" },
      }),
    ).toBeNull();
  });

  it("rejects non-finite answer probabilities", () => {
    expect(validateAnswerProbability(0.5)).toBe(true);
    expect(validateAnswerProbability(Number.NaN)).toBe(false);
    expect(validateAnswerProbability(1.2)).toBe(false);
  });

  it("classifies retryable judgment failures", () => {
    expect(isRetryableJudgmentFailure("timeout")).toBe(true);
    expect(isRetryableJudgmentFailure("authentication")).toBe(false);
  });
});

describe("operation authority", () => {
  it("approves only when hash, case version, write toggle, and action version match", () => {
    const pending = samplePending();
    expect(
      tryApprove(pending, {
        type: "ApproveOperation",
        operationId: "op1",
        expectedCanonicalHash: "abc",
        expectedCaseVersion: 3,
      }),
    ).toEqual({ ok: true });
    expect(
      tryApprove(samplePending({ writeActionEnabled: false }), {
        type: "ApproveOperation",
        operationId: "op1",
        expectedCanonicalHash: "abc",
        expectedCaseVersion: 3,
      }),
    ).toEqual({ ok: false, error: "write_action_disabled" });
  });

  it("rejects without requiring write toggle", () => {
    expect(
      tryReject(samplePending({ writeActionEnabled: false }), {
        type: "RejectOperation",
        operationId: "op1",
        expectedCanonicalHash: "abc",
        expectedCaseVersion: 3,
      }),
    ).toEqual({ ok: true });
  });

  it("guards connection and reflex optimistic versions and scopes", () => {
    expect(tryMutateConnection(2, 2)).toEqual({ ok: true });
    expect(tryMutateConnection(2, 1)).toEqual({
      ok: false,
      error: "connection_version_mismatch",
    });
    expect(tryMutateReflex(4, 5)).toEqual({
      ok: false,
      error: "reflex_state_version_mismatch",
    });
    expect(scopeStillGranted(samplePending(), new Set(["memory.write"]))).toEqual({ ok: true });
    expect(scopeStillGranted(samplePending(), new Set())).toEqual({
      ok: false,
      error: "granted_scope_revoked",
    });
  });
});
