import type { CaseExecutionStepView, CaseExecutionView } from "@relay/contracts";
import type { ReceiptRecord } from "./learning-store.js";
import type { RuntimeEventV2 } from "./runtime-events.js";

export type ProjectCaseExecutionInput = {
  readonly caseId: string | null;
  readonly events: readonly RuntimeEventV2[];
  readonly receipt: ReceiptRecord | null;
  readonly caseStatus: "active" | "waiting" | "completed" | "blocked" | "failed" | null;
  readonly historical: boolean;
};

type StepKey =
  | "source.accept"
  | "case.create"
  | "reflex.detect"
  | "judgment.request"
  | "judgment.response"
  | "policy.evaluate"
  | "model"
  | "answer.committed";

const STEP_LABEL: Readonly<Record<StepKey, string>> = {
  "source.accept": "Input accepted",
  "case.create": "Case created",
  "reflex.detect": "Acronym detection",
  "judgment.request": "Jev request",
  "judgment.response": "Jev response",
  "policy.evaluate": "Policy gate",
  model: "Local model",
  "answer.committed": "Answer committed",
};

/**
 * Project a compact Case execution sequence from canonical trace events.
 * Never invents missing stages. Never sums child durations for totalMs.
 */
export function projectCaseExecution(input: ProjectCaseExecutionInput): CaseExecutionView | null {
  const caseId = input.caseId;
  if (!caseId) return null;

  const ordered = input.events
    .filter((event) => event.caseId === caseId)
    .sort((a, b) => a.sequence - b.sequence || Date.parse(a.at) - Date.parse(b.at));

  if (ordered.length === 0) return null;

  const byType = groupByType(ordered);
  const accepted = firstOf(byType, "source.accepted");
  const answer = firstOf(byType, "answer.committed");
  const caseCreated = firstOf(byType, "case.created");
  const reflex = firstOf(byType, "reflex.detected");
  const judgmentRequest = firstOf(byType, "judgment.requested");
  const judgmentDone =
    lastOf(byType, "judgment.completed") ?? lastOf(byType, "judgment.failed");
  const policy = firstOf(byType, "policy.evaluated");
  const modelRequest = firstOf(byType, "model.requested");
  const modelDone = lastOf(byType, "model.completed") ?? lastOf(byType, "model.failed");

  const jevSkipped = shouldMarkJevSkipped({
    receipt: input.receipt,
    hasJudgment: judgmentRequest != null || judgmentDone != null,
    hasModel: modelRequest != null || modelDone != null,
    hasAnswer: answer != null,
  });

  const rawSteps: Array<{
    key: StepKey;
    event: RuntimeEventV2 | null;
    state: CaseExecutionStepView["state"];
    durationMs: number | null;
  }> = [];

  pushTaken(rawSteps, "source.accept", accepted);
  pushTaken(rawSteps, "case.create", caseCreated);
  pushTaken(rawSteps, "reflex.detect", reflex);

  if (judgmentRequest || judgmentDone) {
    pushTaken(rawSteps, "judgment.request", judgmentRequest);
    if (judgmentDone) {
      pushTaken(rawSteps, "judgment.response", judgmentDone, mapEventState(judgmentDone));
    } else if (judgmentRequest) {
      pushTaken(rawSteps, "judgment.response", null, "waiting");
    }
  } else if (jevSkipped) {
    rawSteps.push({
      key: "judgment.request",
      event: null,
      state: "skipped",
      durationMs: null,
    });
  }

  pushTaken(rawSteps, "policy.evaluate", policy);

  if (modelRequest || modelDone) {
    const modelEvent = modelDone ?? modelRequest;
    rawSteps.push({
      key: "model",
      event: modelEvent,
      state: mapEventState(modelEvent!),
      durationMs: modelDone?.durationMs ?? null,
    });
  }

  pushTaken(rawSteps, "answer.committed", answer);

  const steps = withDeltas(rawSteps);
  // Case response duration is wall clock from source.accepted → answer.committed.
  const totalMs = elapsed(accepted?.at ?? null, answer?.at ?? null);
  const outcome = resolveOutcome({
    hasAnswer: answer != null,
    totalMs,
    caseStatus: input.caseStatus,
    hasOutcome: firstOf(byType, "outcome.recorded") != null,
  });

  return {
    caseId,
    historical: input.historical,
    steps,
    totalMs: outcome === "answered" ? totalMs : null,
    outcome,
  };
}

export function selectCaseIdForExecution(
  events: readonly RuntimeEventV2[],
  activeCaseId: string | null,
): { readonly caseId: string | null; readonly historical: boolean } {
  if (activeCaseId) {
    if (events.some((event) => event.caseId === activeCaseId)) {
      return { caseId: activeCaseId, historical: false };
    }
  }
  const withCase = [...events].reverse().find((event) => event.caseId != null);
  return { caseId: withCase?.caseId ?? null, historical: withCase?.caseId != null };
}

function shouldMarkJevSkipped(input: {
  readonly receipt: ReceiptRecord | null;
  readonly hasJudgment: boolean;
  readonly hasModel: boolean;
  readonly hasAnswer: boolean;
}): boolean {
  if (input.hasJudgment) return false;
  const receipt = input.receipt;
  if (!receipt) {
    // Direct local-model answers never entered Jev — show skipped only when the
    // model path (or a committed answer without judgment) is present.
    return input.hasModel || input.hasAnswer;
  }
  if (
    receipt.questionType === "not_applicable" ||
    receipt.questionType === "deterministic" ||
    receipt.provider === "not_applicable" ||
    receipt.reasonCode === "exact_glossary" ||
    receipt.reasonCode === "hosted_processing_disabled"
  ) {
    return true;
  }
  return input.hasModel || input.hasAnswer;
}

function resolveOutcome(input: {
  readonly hasAnswer: boolean;
  readonly totalMs: number | null;
  readonly caseStatus: ProjectCaseExecutionInput["caseStatus"];
  readonly hasOutcome: boolean;
}): CaseExecutionView["outcome"] {
  if (input.hasAnswer && input.totalMs != null) return "answered";
  if (input.caseStatus === "blocked") return "blocked";
  if (input.caseStatus === "failed") return "failed";
  if (input.caseStatus === "completed" || input.hasOutcome) return "resolved_without_answer";
  if (input.caseStatus === "active" || input.caseStatus === "waiting" || input.caseStatus == null) {
    return "in_progress";
  }
  return null;
}

function pushTaken(
  steps: Array<{
    key: StepKey;
    event: RuntimeEventV2 | null;
    state: CaseExecutionStepView["state"];
    durationMs: number | null;
  }>,
  key: StepKey,
  event: RuntimeEventV2 | null | undefined,
  state?: CaseExecutionStepView["state"],
): void {
  if (!event && state !== "waiting") return;
  if (!event) {
    steps.push({ key, event: null, state: state ?? "waiting", durationMs: null });
    return;
  }
  steps.push({
    key,
    event,
    state: state ?? mapEventState(event),
    durationMs: event.durationMs ?? null,
  });
}

function withDeltas(
  raw: ReadonlyArray<{
    key: StepKey;
    event: RuntimeEventV2 | null;
    state: CaseExecutionStepView["state"];
    durationMs: number | null;
  }>,
): CaseExecutionStepView[] {
  const steps: CaseExecutionStepView[] = [];
  let previousAt: string | null = null;
  for (const item of raw) {
    let deltaMs: number | null = null;
    if (item.state === "skipped") {
      deltaMs = null;
    } else if (item.event) {
      if (previousAt == null) {
        deltaMs = 0;
      } else {
        deltaMs = elapsed(previousAt, item.event.at);
      }
      previousAt = item.event.at;
    }
    steps.push({
      key: item.key,
      label: item.key === "judgment.request" && item.state === "skipped" ? "Jev" : STEP_LABEL[item.key],
      state: item.state,
      deltaMs,
      durationMs: item.durationMs,
    });
  }
  return steps;
}

function mapEventState(event: RuntimeEventV2): CaseExecutionStepView["state"] {
  if (event.status === "failed") return "failed";
  if (event.status === "waiting") return "waiting";
  if (event.status === "started") return "running";
  return "passed";
}

function groupByType(events: readonly RuntimeEventV2[]): Map<string, RuntimeEventV2[]> {
  const map = new Map<string, RuntimeEventV2[]>();
  for (const event of events) {
    const list = map.get(event.eventType) ?? [];
    list.push(event);
    map.set(event.eventType, list);
  }
  return map;
}

function firstOf(map: Map<string, RuntimeEventV2[]>, type: string): RuntimeEventV2 | null {
  return map.get(type)?.[0] ?? null;
}

function lastOf(map: Map<string, RuntimeEventV2[]>, type: string): RuntimeEventV2 | null {
  const list = map.get(type);
  if (!list || list.length === 0) return null;
  return list[list.length - 1]!;
}

function elapsed(startAt: string | null, endAt: string | null): number | null {
  if (!startAt || !endAt) return null;
  const start = Date.parse(startAt);
  const end = Date.parse(endAt);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return null;
  return end - start;
}
