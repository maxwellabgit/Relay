/**
 * Derive live-summary fields from structural events.
 * The backoff matches runtime-events backoffMs so a waiting judgment
 * projects the same next attempt the dispatcher will use.
 */

export type DiagnosticObservation = {
  readonly at: string;
  readonly eventType: string;
  readonly status?: string | null;
  readonly reasonCode?: string | null;
  readonly queueDepth?: number | null;
  readonly caseId?: string | null;
  readonly stage?: string | null;
  readonly attempt?: number | null;
  readonly reflexId?: string | null;
  readonly toolId?: string | null;
};

export type ObservedDiagnostics = {
  readonly queueReady: number;
  readonly oldestReadyMs: number;
  readonly deadLetters: number;
  readonly activeCases: number;
  readonly waitingCases: number;
  readonly failedCases: number;
  readonly latestCase: {
    readonly caseId: string | null;
    readonly stage: string | null;
    readonly blocker: string | null;
  };
  readonly latestJudgment: {
    readonly questionSet: string | null;
    readonly selectedRoute: string | null;
    readonly policyResult: string | null;
    readonly observed: boolean;
  };
  readonly latestTool: {
    readonly toolId: string | null;
    readonly result: string | null;
    readonly observed: boolean;
  };
  readonly retrySchedule: string | null;
  readonly providerReadiness: {
    readonly jev: string;
    readonly model: string;
    readonly audio: string;
  };
};

type CaseRollup = {
  stage: string | null;
  status: "active" | "waiting" | "failed";
  blocker: string | null;
};

function backoffMs(attempt: number): number {
  return Math.min(8_000, 200 * 2 ** Math.max(0, attempt - 1));
}

export function observeDiagnostics(
  events: readonly DiagnosticObservation[],
  nowMs: number,
): ObservedDiagnostics {
  let queueReady = 0;
  let readySince: number | null = null;
  let deadLetters = 0;
  const cases = new Map<string, CaseRollup>();
  let latestJudgment: ObservedDiagnostics["latestJudgment"] = {
    questionSet: null,
    selectedRoute: null,
    policyResult: null,
    observed: false,
  };
  let latestTool: ObservedDiagnostics["latestTool"] = {
    toolId: null,
    result: null,
    observed: false,
  };
  let retrySchedule: string | null = null;
  let jev = "not_observed";
  let model = "not_observed";
  const audio = "not_observed";

  for (const event of events) {
    if (typeof event.queueDepth === "number") {
      if (queueReady === 0 && event.queueDepth > 0) {
        const at = Date.parse(event.at);
        readySince = Number.isFinite(at) ? at : readySince;
      }
      if (event.queueDepth === 0) readySince = null;
      queueReady = event.queueDepth;
    }

    if (event.eventType === "work.failed" || event.reasonCode === "dead_letter") {
      deadLetters += 1;
    }

    if (event.caseId) {
      const current = cases.get(event.caseId) ?? {
        stage: event.stage ?? null,
        status: "active" as const,
        blocker: null,
      };
      const stage = event.stage ?? current.stage;
      let status = current.status;
      let blocker = current.blocker;
      if (event.eventType === "answer.committed" || event.reasonCode === "cancelled") {
        cases.delete(event.caseId);
      } else {
        if (event.status === "failed" || event.eventType === "work.failed") {
          status = "failed";
          blocker = event.reasonCode ?? blocker;
        } else if (event.status === "waiting") {
          status = "waiting";
          blocker = event.reasonCode ?? event.stage ?? blocker;
        }
        cases.set(event.caseId, { stage, status, blocker });
      }
    }

    if (
      event.eventType === "judgment.requested" ||
      event.eventType === "judgment.completed" ||
      event.eventType === "judgment.failed"
    ) {
      latestJudgment = {
        questionSet: event.reflexId ?? latestJudgment.questionSet,
        selectedRoute: event.reasonCode ?? null,
        policyResult: event.status ?? null,
        observed: true,
      };
      jev = event.status === "failed" ? "degraded" : "observed";
    }
    if (event.eventType === "policy.evaluated") {
      latestJudgment = {
        ...latestJudgment,
        policyResult: event.reasonCode ?? event.status ?? latestJudgment.policyResult,
        observed: true,
      };
    }
    if (
      (event.eventType === "tool.routed" || event.eventType === "tool.completed") &&
      event.toolId
    ) {
      latestTool = {
        toolId: event.toolId,
        result: event.reasonCode ?? event.status ?? null,
        observed: true,
      };
    }
    if (event.eventType.startsWith("model.")) {
      model = event.status === "failed" ? "degraded" : "observed";
    }
    if (event.eventType === "judgment.failed" && event.status === "waiting" && event.attempt) {
      const at = Date.parse(event.at);
      if (Number.isFinite(at)) {
        retrySchedule = new Date(at + backoffMs(event.attempt)).toISOString();
      }
    }
    if (event.eventType === "judgment.completed" || event.eventType === "answer.committed") {
      retrySchedule = null;
    }
  }

  const oldestReadyMs =
    queueReady > 0 && readySince != null ? Math.max(0, nowMs - readySince) : 0;

  let activeCases = 0;
  let waitingCases = 0;
  let failedCases = 0;
  let latestCase: ObservedDiagnostics["latestCase"] = {
    caseId: null,
    stage: null,
    blocker: null,
  };
  for (const [caseId, info] of cases) {
    if (info.status === "failed") failedCases += 1;
    else if (info.status === "waiting") waitingCases += 1;
    else activeCases += 1;
    latestCase = { caseId, stage: info.stage, blocker: info.blocker };
  }

  return {
    queueReady,
    oldestReadyMs,
    deadLetters,
    activeCases,
    waitingCases,
    failedCases,
    latestCase,
    latestJudgment,
    latestTool,
    retrySchedule,
    providerReadiness: { jev, model, audio },
  };
}
