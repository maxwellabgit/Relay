import type { DecisionReceiptView, TraceRow } from "@relay/contracts";

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
  readonly status: "taken" | "idle" | "terminal";
  readonly branch?: JevTreeBranch;
};

export type JevTreeView = {
  readonly totalMs: number | null;
  readonly nodes: readonly JevTreeNode[];
  readonly output: Readonly<Record<string, string | number | boolean | null>>;
  readonly completed: boolean;
};

export function projectJevTree(
  gate: DecisionReceiptView | null,
  trace: readonly TraceRow[],
): JevTreeView {
  const relevant = filterRelevant(trace, gate);
  const askedJev = relevant.some(
    (row) => row.stage === "judgment.request" || row.type === "judgment.requested",
  );
  const choiceGate = gate?.questionType === "choice";
  const nodes: JevTreeNode[] = [];
  let step = 1;

  nodes.push({
    step: step++,
    title: "Request received",
    detail: "Typed or fixture source accepted",
    durationMs: durationFor(relevant, "source.accept"),
    status: "taken",
  });

  nodes.push({
    step: step++,
    title: "Intent check",
    detail: "Acronym / birthday / memory detect",
    durationMs: durationFor(relevant, "reflex.detect"),
    status: "taken",
  });

  const needJevExit = !(askedJev || choiceGate);
  nodes.push({
    step: step++,
    title: "Need Jev Choice?",
    detail: "Ambiguous options require a gate",
    durationMs: durationFor(relevant, "judgment.request"),
    status: "taken",
    branch: {
      continueLabel: "Yes",
      exitLabel: needJevExit
        ? gate?.nextAction === "local_result"
          ? "Answer from glossary"
          : "Local / search path"
        : "Answer without Jev",
      exitTaken: needJevExit,
      exitDurationMs: needJevExit
        ? durationFor(relevant, "policy.evaluate") ?? durationFor(relevant, "reflex.detect")
        : null,
    },
  });

  if (!needJevExit) {
    nodes.push({
      step: step++,
      title: "Jev response",
      detail: "Choice probabilities from provider",
      durationMs: durationFor(relevant, "judgment.response") ?? gate?.latencyMs ?? null,
      status: "taken",
      branch: {
        continueLabel: "Ok",
        exitLabel:
          gate?.reasonCode === "missing_secret"
            ? "Block · missing key"
            : gate?.reasonCode === "rate_limited"
              ? "Retry / wait"
              : "Provider fail",
        exitTaken: gate?.result === "fail" && (gate.reasonCode === "missing_secret" || gate.reasonCode === "rate_limited"),
        exitDurationMs: gate?.latencyMs ?? null,
      },
    });

    const policyFail = gate?.result === "fail" && gate.reasonCode !== "missing_secret" && gate.reasonCode !== "rate_limited";
    nodes.push({
      step: step++,
      title: "Threshold gate",
      detail: "Min probability · margin · allowed options",
      durationMs: durationFor(relevant, "policy.evaluate"),
      status: "taken",
      branch: {
        continueLabel: "Pass",
        exitLabel: gate ? humanReason(gate.reasonCode) : "Below threshold",
        exitTaken: policyFail === true || gate?.result === "wait",
        exitDurationMs: gate?.latencyMs ?? null,
      },
    });

    nodes.push({
      step: step++,
      title: "Next engine action",
      detail: "Feed answer, wait, or block",
      durationMs: durationFor(relevant, "proposal.create"),
      status: "taken",
    });
  }

  nodes.push({
    step: step,
    title: "Jev decision",
    detail: terminalLabel(gate),
    durationMs: gate?.latencyMs ?? null,
    status: "terminal",
  });

  const totalMs =
    sumDurations(nodes) ??
    (gate?.latencyMs != null ? gate.latencyMs : null) ??
    spanTotal(relevant);

  return {
    totalMs,
    nodes,
    output: safeOutput(gate),
    completed: gate != null && gate.result !== "wait",
  };
}

function filterRelevant(trace: readonly TraceRow[], gate: DecisionReceiptView | null): readonly TraceRow[] {
  const caseRows = gate ? trace.filter((row) => row.caseId != null) : trace;
  if (caseRows.length > 0) return caseRows;
  return trace;
}

function durationFor(rows: readonly TraceRow[], stage: string): number | null {
  const matches = rows.filter((row) => row.stage === stage && row.durationMs != null);
  if (matches.length === 0) return null;
  return matches.reduce((sum, row) => sum + (row.durationMs ?? 0), 0);
}

function sumDurations(nodes: readonly JevTreeNode[]): number | null {
  const values = nodes.map((node) => node.durationMs).filter((value): value is number => value != null);
  if (values.length === 0) return null;
  return values.reduce((a, b) => a + b, 0);
}

function spanTotal(rows: readonly TraceRow[]): number | null {
  if (rows.length < 2) return rows[0]?.durationMs ?? null;
  const first = Date.parse(rows[0]!.at);
  const last = Date.parse(rows[rows.length - 1]!.at);
  if (Number.isNaN(first) || Number.isNaN(last)) return null;
  return Math.max(0, last - first);
}

function terminalLabel(gate: DecisionReceiptView | null): string {
  if (!gate) return "Waiting for a bounded judgment";
  if (gate.questionType === "choice" && gate.result === "pass") {
    const selected = Object.entries(gate.probabilities).sort((a, b) => b[1] - a[1])[0]?.[0];
    const label = selected ? gate.optionLabels[selected] ?? selected : "selected option";
    return `Respond with Choice · ${label}`;
  }
  if (gate.result === "fail") return `Fail · ${humanReason(gate.reasonCode)}`;
  if (gate.result === "wait") return `Wait · ${humanReason(gate.reasonCode)}`;
  if (gate.result === "not_applicable") return gate.nextAction.replaceAll("_", " ");
  return `${gate.result} · ${gate.nextAction}`;
}

function humanReason(code: string): string {
  return code.replaceAll("_", " ");
}

function safeOutput(gate: DecisionReceiptView | null): Readonly<Record<string, string | number | boolean | null>> {
  if (!gate) {
    return { decision: null, result: null, provider: null };
  }
  const top = Object.entries(gate.probabilities).sort((a, b) => b[1] - a[1])[0];
  return {
    decision: gate.nextAction,
    result: gate.result,
    reasonCode: gate.reasonCode,
    questionType: gate.questionType,
    provider: gate.provider,
    selectedOption: top?.[0] ?? null,
    topProbability: top?.[1] ?? null,
    margin: gate.margin,
    retries: gate.retries,
    latencyMs: gate.latencyMs,
  };
}
