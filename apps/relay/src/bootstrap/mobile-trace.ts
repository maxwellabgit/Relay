import type { ByteFilePort } from "@relay/adapter-expo";
import { isTraceEvent, type RuntimeEventV2, type TraceSink } from "@relay/engine";

const MAX_EVENTS = 200;

/** Bounded on-device trace. Does not post to the browser trace route. */
export function createMobileTraceSink(files: ByteFilePort, runId: string): TraceSink {
  const name = `trace-${runId}.jsonl`;
  return {
    runId,
    directoryLabel: name,
    async append(event) {
      if (!isTraceEvent(event)) throw new Error("trace_rejected");
      const lines = await readLines(files, name);
      lines.push(JSON.stringify(event));
      await writeLines(files, name, lines.slice(-MAX_EVENTS));
    },
    async read() {
      const lines = await readLines(files, name);
      const kept: RuntimeEventV2[] = [];
      for (const line of lines) {
        try {
          const parsed: unknown = JSON.parse(line);
          if (isTraceEvent(parsed)) kept.push(parsed);
        } catch {
          // Drop a malformed line.
        }
      }
      return kept;
    },
  };
}

async function readLines(files: ByteFilePort, name: string): Promise<string[]> {
  const bytes = await files.read(name);
  if (!bytes || bytes.byteLength === 0) return [];
  return new TextDecoder().decode(bytes).split("\n").filter((line) => line.trim().length > 0);
}

async function writeLines(files: ByteFilePort, name: string, lines: readonly string[]): Promise<void> {
  const body = lines.length === 0 ? "" : `${lines.join("\n")}\n`;
  await files.write(name, new TextEncoder().encode(body));
}
