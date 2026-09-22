import { describe, expect, it } from "vitest";
import {
  copyableRunIds,
  exportTraceSelection,
  filterConsoleTrace,
  traceProvider,
  traceSeverity,
  type ConsoleTraceRow,
} from "./console-trace.js";

const rows: ConsoleTraceRow[] = [
  {
    sequence: 1,
    type: "case.started",
    stage: "model.request",
    status: "ok",
    reasonCode: null,
    caseId: "case_a",
    runId: "run_1",
  },
  {
    sequence: 2,
    type: "jev.requested",
    stage: "judgment.request",
    status: "waiting",
    reasonCode: "retry",
    caseId: "case_a",
    runId: "run_1",
  },
  {
    sequence: 3,
    type: "case.failed",
    stage: "tool.execute",
    status: "failed",
    reasonCode: "cancelled",
    caseId: "case_b",
    runId: "run_2",
  },
];

const open = {
  runId: null,
  caseId: null,
  provider: null,
  severity: null,
  stage: null,
  status: null,
  reason: null,
};

describe("console trace filters", () => {
  it("classifies provider and severity from canonical fields", () => {
    expect(traceProvider(rows[0]!)).toBe("model");
    expect(traceProvider(rows[1]!)).toBe("jev");
    expect(traceProvider(rows[2]!)).toBe("local");
    expect(traceSeverity(rows[1]!)).toBe("warn");
    expect(traceSeverity(rows[2]!)).toBe("error");
    expect(traceProvider({ type: "halo.frame", stage: "halo.draw" })).toBe("halo");
  });

  it("filters by run, case, provider, and severity together", () => {
    expect(filterConsoleTrace(rows, { ...open, runId: "run_1" }).map((row) => row.sequence)).toEqual([
      1, 2,
    ]);
    expect(
      filterConsoleTrace(rows, { ...open, caseId: "case_a", provider: "jev" }).map((row) => row.sequence),
    ).toEqual([2]);
    expect(filterConsoleTrace(rows, { ...open, severity: "error" }).map((row) => row.sequence)).toEqual([
      3,
    ]);
  });

  it("copies ids and exports the filtered selection without prose", () => {
    const ids = copyableRunIds({
      runId: "run_1",
      caseId: "case_a",
      decisionId: null,
      sessionId: "session_1",
    });
    expect(ids).toContain("run run_1");
    expect(ids).toContain("decision none");
    const exported = exportTraceSelection([rows[2]!]);
    expect(exported).toContain("\"severity\": \"error\"");
    expect(exported).not.toContain("transcript");
  });
});