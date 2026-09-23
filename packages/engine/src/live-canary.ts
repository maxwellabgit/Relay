import type { JudgmentPort, JudgmentRequest, JudgmentResponse } from "@relay/contracts";
import { JEV_MODEL } from "./typesafe-judgment.js";
import { JevHealthTracker, type JevHealthState } from "./jev-health.js";

const KINDS = ["noul", "choice", "score"] as const;
type CanaryKind = (typeof KINDS)[number];

export type LiveCanaryRow = {
  readonly kind: CanaryKind;
  readonly ok: boolean;
  readonly status: number | null;
  readonly category: string;
  readonly requestId: string | null;
};

export type LiveCanaryReceipt = {
  readonly ok: boolean;
  readonly evidence: "packaged_app";
  readonly health: JevHealthState;
  readonly rows: readonly LiveCanaryRow[];
};

/**
 * Three-kind connectivity probe through the app's JudgmentPort.
 * A direct HTTP client must not call this. Success is the only path to noteLiveCanary.
 */
export async function runPackagedLiveCanary(
  port: JudgmentPort,
  tracker: JevHealthTracker,
  signal: AbortSignal,
): Promise<LiveCanaryReceipt> {
  const rows: LiveCanaryRow[] = [];
  for (const kind of KINDS) {
    if (signal.aborted) {
      const health = tracker.noteUnavailable();
      return { ok: false, evidence: "packaged_app", health, rows };
    }
    const response = await port.judge(canaryRequest(kind), signal);
    rows.push(rowFrom(kind, response));
    if (!response.ok) {
      const category = response.failure.category;
      const health =
        category === "network" || category === "timeout" || category === "cancelled"
          ? tracker.noteUnavailable()
          : tracker.noteFailure();
      return { ok: false, evidence: "packaged_app", health, rows };
    }
  }
  return {
    ok: true,
    evidence: "packaged_app",
    health: tracker.noteLiveCanary(),
    rows,
  };
}

function canaryRequest(kind: CanaryKind): JudgmentRequest {
  if (kind === "noul") {
    return {
      questionSetId: "canary.noul",
      questionSetVersion: "1",
      model: JEV_MODEL,
      state: { purpose: "connectivity", kind },
      questions: {
        useful: { type: "noul", instructions: "Is this a connectivity check?" },
      },
    };
  }
  if (kind === "choice") {
    return {
      questionSetId: "canary.choice",
      questionSetVersion: "1",
      model: JEV_MODEL,
      state: { purpose: "connectivity", kind },
      questions: {
        pick: {
          type: "choice",
          instructions: "Pick one.",
          criteria: { yes: "Yes", no: "No" },
        },
      },
    };
  }
  return {
    questionSetId: "canary.score",
    questionSetVersion: "1",
    model: JEV_MODEL,
    state: { purpose: "connectivity", kind },
    questions: {
      rank: {
        type: "score",
        instructions: "Score clarity from 0 to 1.",
        criteria: ["clarity"],
      },
    },
  };
}

function rowFrom(kind: CanaryKind, response: JudgmentResponse): LiveCanaryRow {
  if (!response.ok) {
    return {
      kind,
      ok: false,
      status: response.failure.httpStatus ?? response.failure.transport?.status ?? null,
      category: response.failure.category,
      requestId: response.failure.providerRequestId ?? null,
    };
  }
  return {
    kind,
    ok: true,
    status: response.success.transport?.status ?? null,
    category: "ok",
    requestId: response.success.providerRequestId ?? null,
  };
}
