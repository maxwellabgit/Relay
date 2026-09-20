import type { DecisionAttemptView, DecisionRunView, DecisionStageState, DecisionStageView } from "@relay/contracts";
import type { ReceiptRecord } from "./learning-store.js";
import { labelsOrUnavailable } from "./protected-content.js";
import type { RuntimeEventV2 } from "./runtime-events.js";

const STAGE_ORDER = [
  "source.accept",
  "reflex.detect",
  "judgment.request",
  "judgment.response",
  "policy.evaluate",
  "proposal.create",
] as const;

const STAGE_TITLE: Readonly<Record<(typeof STAGE_ORDER)[number], string>> = {
  "source.accept": "Request received",
  "reflex.detect": "Intent check",
  "judgment.request": "Need Jev Choice?",
  "judgment.response": "Jev response",
  "policy.evaluate": "Threshold gate",
  "proposal.create": "Next engine action",
};

export type ProjectDecisionRunInput = {
  readonly receipt: ReceiptRecord | null;
  readonly events: readonly RuntimeEventV2[];
  readonly activeCaseId: string | null;
  readonly historical: boolean;
};

/**
 * Project a correlated decision for the console.
 * Never invents taken stages. Never sums stage durations for elapsedMs.
 * Receipts without caseId are rejected.
 */
export function projectDecisionRun(input: ProjectDecisionRunInput): DecisionRunView | null {
  const receipt = input.receipt;
  if (!receipt) return null;
  if (!receipt.caseId) return null;

  const decisionId = receipt.decisionId || receipt.receiptId;
  const correlated = correlateEvents(input.events, {
    caseId: receipt.caseId,
    decisionId,
    receiptId: receipt.receiptId,
    judgmentId: receipt.judgmentId,
  });
  const ordered = [...correlated].sort((a, b) => a.sequence - b.sequence || Date.parse(a.at) - Date.parse(b.at));

  const localOnly =
    receipt.questionType === "not_applicable" ||
    receipt.questionType === "deterministic" ||
    receipt.reasonCode === "exact_glossary" ||
    receipt.provider === "not_applicable";

  const stages = buildStages(ordered, receipt, localOnly);
  const attempts = buildAttempts(ordered);
  const elapsedMs = elapsed(receipt.requestedAt, receipt.completedAt);
  const probabilities = receipt.probabilities;
  const values = Object.values(probabilities);
  const top = values.length === 0 ? null : Math.max(...values);
  const ranked = [...values].sort((a, b) => b - a);
  const margin = ranked.length === 0 ? null : ranked.length === 1 ? ranked[0]! : ranked[0]! - ranked[1]!;

  const optionIds =
    Object.keys(probabilities).length > 0 ? Object.keys(probabilities) : Object.keys(receipt.optionLabels);
  return {
    decisionId,
    receiptId: receipt.receiptId,
    caseId: receipt.caseId,
    judgmentId: receipt.judgmentId,
    reflexId: receipt.reflexId ?? (receipt.gateId.startsWith("reflex.") ? receipt.gateId : null),
    gateId: receipt.gateId,
    policyVersion: receipt.policyVersion,
    historical: input.historical,
    questionType: receipt.questionType,
    status: mapStatus(receipt),
    stages,
    optionIds,
    optionLabels: labelsOrUnavailable(optionIds, receipt.optionLabels),
    probabilities,
    thresholds: receipt.thresholds,
    topProbability: top,
    margin,
    selectedOptionId: receipt.selectedOptionId ?? receipt.selectedOption,
    reasonCode: receipt.reasonCode,
    nextAction: nextAction(receipt),
    provider: receipt.provider,
    attempts,
    requestedAt: receipt.requestedAt,
    completedAt: receipt.completedAt,
    elapsedMs,
    result: receipt.result,
  };
}

export function selectReceiptForDecision(
  receipts: readonly ReceiptRecord[],
  activeCaseId: string | null,
): { readonly receipt: ReceiptRecord | null; readonly historical: boolean } {
  const withCase = receipts.filter((receipt) => receipt.caseId != null);
  if (activeCaseId) {
    const active = [...withCase].reverse().find((receipt) => receipt.caseId === activeCaseId) ?? null;
    if (active) return { receipt: active, historical: false };
  }
  const latest = withCase.at(-1) ?? null;
  return { receipt: latest, historical: latest != null };
}

function correlateEvents(
  events: readonly RuntimeEventV2[],
  ids: {
    readonly caseId: string;
    readonly decisionId: string;
    readonly receiptId: string;
    readonly judgmentId: string | null;
  },
): RuntimeEventV2[] {
  return events.filter((event) => {
    if (event.caseId != null && event.caseId !== ids.caseId) return false;
    if (event.receiptId != null && event.receiptId !== ids.receiptId) return false;
    if (event.judgmentId != null && ids.judgmentId != null && event.judgmentId !== ids.judgmentId) return false;
    if ("decisionId" in event && (event as { decisionId?: string }).decisionId != null) {
      return (event as { decisionId?: string }).decisionId === ids.decisionId;
    }
    // Case-scoped events without decisionId still belong when case matches.
    return event.caseId === ids.caseId;
  });
}

function buildStages(
  events: readonly RuntimeEventV2[],
  receipt: ReceiptRecord,
  localOnly: boolean,
): DecisionStageView[] {
  const byStage = new Map<string, RuntimeEventV2[]>();
  for (const event of events) {
    const list = byStage.get(event.stage) ?? [];
    list.push(event);
    byStage.set(event.stage, list);
  }

  const stages: DecisionStageView[] = [];
  for (const stage of STAGE_ORDER) {
    if (localOnly && (stage === "judgment.request" || stage === "judgment.response")) {
      if (byStage.has(stage)) {
        // Should not happen for local-only; if present, show as skipped preference.
      }
      stages.push({
        stage,
        title: STAGE_TITLE[stage],
        state: "skipped",
        durationMs: null,
        reasonCode: "exact_glossary",
        ...(stage === "judgment.request"
          ? {
              branch: {
                continueLabel: "Yes",
                exitLabel: "Answer locally",
                taken: "exit" as const,
              },
            }
          : {}),
      });
      continue;
    }

    const rows = byStage.get(stage) ?? [];
    if (rows.length === 0) {
      // Only include stages that actually occurred, or terminal policy for choice receipts.
      if (stage === "policy.evaluate" && receipt.questionType === "choice") {
        stages.push({
          stage,
          title: STAGE_TITLE[stage],
          state: mapResultState(receipt),
          durationMs: null,
          reasonCode: receipt.reasonCode,
          branch: {
            continueLabel: "Pass",
            exitLabel: receipt.reasonCode.replaceAll("_", " "),
            taken: receipt.result === "pass" ? "continue" : "exit",
          },
        });
      }
      continue;
    }

    const last = rows[rows.length - 1]!;
    const state = mapEventState(last, receipt, stage);
    const durationMs = last.durationMs ?? null;
    const branch =
      stage === "judgment.request"
        ? {
            continueLabel: "Yes",
            exitLabel: "Answer without Jev",
            taken: "continue" as const,
          }
        : stage === "policy.evaluate"
          ? {
              continueLabel: "Pass",
              exitLabel: (last.reasonCode ?? receipt.reasonCode).replaceAll("_", " "),
              taken: receipt.result === "pass" ? ("continue" as const) : ("exit" as const),
            }
          : undefined;

    stages.push({
      stage,
      title: STAGE_TITLE[stage],
      state,
      durationMs,
      reasonCode: last.reasonCode ?? null,
      ...(branch ? { branch } : {}),
    });
  }
  return stages;
}

function buildAttempts(events: readonly RuntimeEventV2[]): DecisionAttemptView[] {
  const attempts: DecisionAttemptView[] = [];
  for (const event of events) {
    if (event.attempt == null) continue;
    if (event.stage !== "judgment.response" && event.eventType !== "judgment.failed" && event.eventType !== "judgment.completed") {
      continue;
    }
    attempts.push({
      attempt: event.attempt,
      status: event.status,
      reasonCode: event.reasonCode ?? null,
      durationMs: event.durationMs ?? null,
      at: event.at,
    });
  }
  return attempts;
}

function elapsed(requestedAt: string | null, completedAt: string | null): number | null {
  if (!requestedAt || !completedAt) return null;
  const start = Date.parse(requestedAt);
  const end = Date.parse(completedAt);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return null;
  return end - start;
}

function mapStatus(receipt: ReceiptRecord): DecisionRunView["status"] {
  if (receipt.result === "blocked") return "blocked";
  if (receipt.result === "wait") return "waiting";
  if (receipt.result === "fail") return "failed";
  if (receipt.result === "pass" || receipt.result === "not_applicable") return "completed";
  return "passed";
}

function mapResultState(receipt: ReceiptRecord): DecisionStageState {
  if (receipt.result === "wait") return "waiting";
  if (receipt.result === "fail" || receipt.result === "blocked") return "failed";
  if (receipt.result === "pass" || receipt.result === "not_applicable") return "passed";
  return "not_started";
}

function mapEventState(event: RuntimeEventV2, receipt: ReceiptRecord, stage: string): DecisionStageState {
  if (event.status === "waiting") return "waiting";
  if (event.status === "failed") return "failed";
  if (event.status === "started") return "running";
  if (stage === "policy.evaluate") return mapResultState(receipt);
  return "passed";
}

function nextAction(receipt: ReceiptRecord): string {
  if (receipt.result === "wait") return "bounded_wait";
  if (receipt.result === "blocked") return "blocked";
  if (receipt.result === "fail") return "no_definition";
  if (receipt.result === "not_applicable") return "local_result";
  if (receipt.questionType === "user") return "stored";
  return "continue";
}
