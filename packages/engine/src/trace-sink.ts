import type { TraceEventV1 } from "@relay/contracts";
import { isRuntimeEvent, type RuntimeEventV2 } from "./runtime-events.js";

export type TraceSink = {
  readonly runId: string;
  readonly directoryLabel: string;
  append(event: RuntimeEventV2): Promise<void>;
  read(): Promise<readonly RuntimeEventV2[]>;
};

export function isTraceEvent(value: unknown): value is RuntimeEventV2 {
  return isRuntimeEvent(value);
}

export type { TraceEventV1 };
