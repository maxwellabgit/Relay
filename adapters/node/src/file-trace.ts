import { appendFile, mkdir, readFile, rename, stat } from "node:fs/promises";
import { join } from "node:path";
import type { TraceEventV1 } from "@relay/contracts";
import { isTraceEvent, type TraceSink } from "@relay/engine";

const ROTATE_AT_BYTES = 1_000_000;

export function createFileTraceSink(runsRoot: string, runId = `run_${Date.now().toString(36)}`): TraceSink {
  const directory = join(runsRoot, runId);
  const file = join(directory, "events.jsonl");
  return {
    runId,
    directoryLabel: file,
    async append(event) {
      if (!isTraceEvent(event)) throw new Error("trace_rejected");
      await mkdir(directory, { recursive: true });
      await rotateIfNeeded(file);
      await appendFile(file, `${JSON.stringify(allow(event))}\n`, "utf8");
    },
    async read() {
      try {
        const text = await readFile(file, "utf8");
        const kept: TraceEventV1[] = [];
        for (const line of text.split("\n")) {
          const trimmed = line.trim();
          if (!trimmed) continue;
          try {
            const parsed: unknown = JSON.parse(trimmed);
            if (isTraceEvent(parsed)) kept.push(parsed);
          } catch {
            // Drop a malformed line.
          }
        }
        return kept;
      } catch {
        return [];
      }
    },
  };
}

function allow(event: TraceEventV1): TraceEventV1 {
  return {
    schemaVersion: 1,
    sequence: event.sequence,
    at: event.at,
    type: event.type,
    ...(event.caseId ? { caseId: event.caseId } : {}),
    ...(event.segmentId ? { segmentId: event.segmentId } : {}),
    ...(event.judgmentId ? { judgmentId: event.judgmentId } : {}),
    ...(event.reflexId ? { reflexId: event.reflexId } : {}),
    ...(event.reflexVersion != null ? { reflexVersion: event.reflexVersion } : {}),
    ...(event.probabilities ? { probabilities: event.probabilities } : {}),
    ...(event.thresholds ? { thresholds: event.thresholds } : {}),
    ...(event.selectedOutcome ? { selectedOutcome: event.selectedOutcome } : {}),
    ...(event.reasonCode ? { reasonCode: event.reasonCode } : {}),
    ...(event.latencyMs != null ? { latencyMs: event.latencyMs } : {}),
    ...(event.artifactRef ? { artifactRef: event.artifactRef } : {}),
    ...(event.queueDepth != null ? { queueDepth: event.queueDepth } : {}),
    ...(event.waitState ? { waitState: event.waitState } : {}),
  };
}

async function rotateIfNeeded(file: string): Promise<void> {
  try {
    const info = await stat(file);
    if (info.size < ROTATE_AT_BYTES) return;
    await rename(file, `${file}.${Date.now()}`);
  } catch {
    // The file does not exist yet.
  }
}
