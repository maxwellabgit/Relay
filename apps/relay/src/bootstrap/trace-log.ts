import type { RuntimeEventV2 } from "@relay/engine";
import { isTraceEvent, type TraceSink } from "@relay/engine";

type TauriInternals = {
  invoke?: (command: string, args: Record<string, unknown>) => Promise<unknown>;
};

export function createBrowserTraceSink(
  runId = `run_${Date.now().toString(36)}`,
  directoryLabel = `.dev-data/runs/${runId}/events.jsonl`,
): TraceSink {
  return {
    runId,
    directoryLabel,
    async append(event) {
      if (!isTraceEvent(event)) throw new Error("trace_rejected");
      const tauri = tauriInternals();
      if (tauri?.invoke) {
        await tauri.invoke("append_trace_event", { runId, line: JSON.stringify(event) });
        return;
      }
      const response = await fetch(`/__relay/trace?runId=${encodeURIComponent(runId)}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(event),
      });
      if (!response.ok) throw new Error("trace_sink_unreachable");
    },
    async read() {
      const tauri = tauriInternals();
      if (tauri?.invoke) {
        const text = await tauri.invoke("read_trace_events", { runId });
        return parseTrace(typeof text === "string" ? text : "");
      }
      try {
        const response = await fetch(`/__relay/trace?runId=${encodeURIComponent(runId)}`);
        if (!response.ok) return [];
        const parsed: unknown = await response.json();
        return Array.isArray(parsed) ? parsed.filter(isTraceEvent) : [];
      } catch {
        return [];
      }
    },
  };
}

function tauriInternals(): TauriInternals | null {
  const host = globalThis as { __TAURI_INTERNALS__?: TauriInternals };
  return host.__TAURI_INTERNALS__ ?? null;
}

function parseTrace(text: string): RuntimeEventV2[] {
  const kept: RuntimeEventV2[] = [];
  for (const line of text.split("\n")) {
    if (!line.trim()) continue;
    try {
      const parsed: unknown = JSON.parse(line);
      if (isTraceEvent(parsed)) kept.push(parsed);
    } catch {
      // Drop a malformed line.
    }
  }
  return kept;
}
