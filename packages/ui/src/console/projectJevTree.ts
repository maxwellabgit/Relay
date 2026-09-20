import type { DecisionRunView } from "@relay/contracts";

export type JevTreeBranch = {
  readonly continueLabel: string;
  readonly exitLabel: string;
  readonly exitTaken: boolean;
  readonly exitDurationMs: number | null;
};

export type JevTreeNode = {
  readonly step: number;
  readonly title: string;
  readonly detail: string;
  readonly durationMs: number | null;
  readonly status: "taken" | "idle" | "terminal" | "skipped" | "waiting" | "failed";
  readonly branch?: JevTreeBranch;
};

export type JevTreeView = {
  readonly totalMs: number | null;
  readonly nodes: readonly JevTreeNode[];
  readonly output: Readonly<Record<string, string | number | boolean | null>>;
  readonly completed: boolean;
  readonly historical: boolean;
  readonly decisionId: string | null;
  readonly caseId: string | null;
};

/**
 * UI adapter over DecisionRunView. Does not invent stages or durations.
 */
export function projectJevTree(decision: DecisionRunView | null): JevTreeView {
  if (!decision) {
    return {
      totalMs: null,
      nodes: [],
      output: { decision: null, result: null, provider: null },
      completed: false,
      historical: false,
      decisionId: null,
      caseId: null,
    };
  }

  const nodes: JevTreeNode[] = decision.stages.map((stage, index) => ({
    step: index + 1,
    title: stage.title,
    detail: stage.reasonCode ?? stage.state,
    durationMs: stage.durationMs,
    status: mapStatus(stage.state),
    ...(stage.branch
      ? {
          branch: {
            continueLabel: stage.branch.continueLabel,
            exitLabel: stage.branch.exitLabel,
            exitTaken: stage.branch.taken === "exit",
            exitDurationMs: null,
          },
        }
      : {}),
  }));

  nodes.push({
    step: nodes.length + 1,
    title: "Jev decision",
    detail: `${decision.result} · ${decision.reasonCode} · ${decision.nextAction}`,
    durationMs: decision.elapsedMs,
    status: "terminal",
  });

  return {
    totalMs: decision.elapsedMs,
    nodes,
    output: {
      decision: decision.nextAction,
      result: decision.result,
      reasonCode: decision.reasonCode,
      questionType: decision.questionType,
      provider: decision.provider,
      selectedOption: decision.selectedOptionId,
      topProbability: decision.topProbability,
      margin: decision.margin,
      retries: decision.attempts.length,
      latencyMs: decision.elapsedMs,
      historical: decision.historical,
    },
    completed: decision.result !== "wait",
    historical: decision.historical,
    decisionId: decision.decisionId,
    caseId: decision.caseId,
  };
}

function mapStatus(
  state: DecisionRunView["stages"][number]["state"],
): JevTreeNode["status"] {
  if (state === "skipped") return "skipped";
  if (state === "waiting") return "waiting";
  if (state === "failed") return "failed";
  if (state === "not_started") return "idle";
  if (state === "running") return "taken";
  return "taken";
}
