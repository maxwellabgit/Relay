import type { TraceEventV1 } from "@relay/contracts";

const ALLOWED = new Set([
  "schemaVersion",
  "sequence",
  "at",
  "type",
  "caseId",
  "segmentId",
  "judgmentId",
  "reflexId",
  "reflexVersion",
  "probabilities",
  "thresholds",
  "selectedOutcome",
  "reasonCode",
  "latencyMs",
  "artifactRef",
  "queueDepth",
  "waitState",
]);

export type TraceSink = {
  readonly runId: string;
  readonly directoryLabel: string;
  append(event: TraceEventV1): Promise<void>;
  read(): Promise<readonly TraceEventV1[]>;
};

export function isTraceEvent(value: unknown): value is TraceEventV1 {
  if (!value || typeof value !== "object") return false;
  const row = value as Record<string, unknown>;
  if (Object.keys(row).some((key) => !ALLOWED.has(key))) return false;
  if (row.schemaVersion !== 1) return false;
  if (typeof row.sequence !== "number" || !Number.isFinite(row.sequence)) return false;
  if (typeof row.at !== "string" || !row.at) return false;
  if (typeof row.type !== "string" || !/^[a-z0-9._]{1,64}$/.test(row.type)) return false;
  if (row.probabilities != null && !isNumberMap(row.probabilities)) return false;
  if (row.thresholds != null && !isNumberMap(row.thresholds)) return false;
  return true;
}

function isNumberMap(value: unknown): value is Record<string, number> {
  if (!value || typeof value !== "object") return false;
  return Object.values(value as Record<string, unknown>).every((item) => typeof item === "number");
}
