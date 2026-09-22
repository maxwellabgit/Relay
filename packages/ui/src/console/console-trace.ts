export type ConsoleTraceRow = {
  readonly sequence: number;
  readonly type: string;
  readonly stage: string | null;
  readonly status: string | null;
  readonly reasonCode: string | null;
  readonly caseId: string | null;
  readonly runId: string | null;
};

export type ConsoleSeverity = "error" | "warn" | "info";

export type ConsoleTraceFilter = {
  readonly runId: string | null;
  readonly caseId: string | null;
  readonly provider: string | null;
  readonly severity: ConsoleSeverity | null;
  readonly stage: string | null;
  readonly status: string | null;
  readonly reason: string | null;
};

export function traceProvider(row: { readonly type: string; readonly stage: string | null }): string {
  const stage = row.stage ?? "";
  if (stage.startsWith("judgment.") || row.type.startsWith("jev.")) return "jev";
  if (stage.startsWith("model.")) return "model";
  if (stage.startsWith("halo.") || row.type.startsWith("halo.")) return "halo";
  return "local";
}

export function traceSeverity(row: { readonly status: string | null }): ConsoleSeverity {
  if (row.status === "failed" || row.status === "error" || row.status === "blocked") return "error";
  if (row.status === "waiting" || row.status === "degraded") return "warn";
  return "info";
}

export function filterConsoleTrace<T extends ConsoleTraceRow>(
  rows: readonly T[],
  filter: ConsoleTraceFilter,
): T[] {
  return rows.filter((row) => {
    if (filter.runId && row.runId !== filter.runId) return false;
    if (filter.caseId && row.caseId !== filter.caseId) return false;
    if (filter.provider && traceProvider(row) !== filter.provider) return false;
    if (filter.severity && traceSeverity(row) !== filter.severity) return false;
    if (filter.stage && row.stage !== filter.stage) return false;
    if (filter.status && row.status !== filter.status) return false;
    if (filter.reason && row.reasonCode !== filter.reason) return false;
    return true;
  });
}

/** Ids only. No transcript or memory text. */
export function copyableRunIds(input: {
  readonly runId: string;
  readonly caseId: string | null;
  readonly decisionId: string | null;
  readonly sessionId: string | null;
}): string {
  return [
    `run ${input.runId || "none"}`,
    `case ${input.caseId ?? "none"}`,
    `decision ${input.decisionId ?? "none"}`,
    `session ${input.sessionId ?? "none"}`,
  ].join("\n");
}

/** Structural selection. No transcript or memory text. */
export function exportTraceSelection(rows: readonly ConsoleTraceRow[]): string {
  return JSON.stringify(
    rows.map((row) => ({
      sequence: row.sequence,
      type: row.type,
      stage: row.stage,
      status: row.status,
      reasonCode: row.reasonCode,
      caseId: row.caseId,
      runId: row.runId,
      provider: traceProvider(row),
      severity: traceSeverity(row),
    })),
    null,
    2,
  );
}
