import { appendFile, mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { TRACE_RETENTION_MS, TRACE_ROTATE_BYTES } from "@relay/engine";
import { isTraceEvent, type TraceSink } from "@relay/engine";
import type { RuntimeEventV2 } from "@relay/engine";

export function createFileTraceSink(runsRoot: string, runId = `run_${Date.now().toString(36)}`): TraceSink {
  const directory = join(runsRoot, runId);
  const file = join(directory, "events.jsonl");
  const startedAt = new Date().toISOString();
  let wroteManifest = false;
  return {
    runId,
    directoryLabel: directory,
    async append(event) {
      if (!isTraceEvent(event)) throw new Error("trace_rejected");
      await mkdir(directory, { recursive: true });
      if (!wroteManifest) {
        await writeFile(
          join(directory, "manifest.json"),
          `${JSON.stringify(
            {
              schemaVersion: 1,
              runId,
              startedAt,
              status: "running",
              retention: "trace 30d/100MB · failed 7d · memories until delete",
            },
            null,
            2,
          )}\n`,
          "utf8",
        );
        wroteManifest = true;
      }
      await compactOldRuns(runsRoot);
      await rotateIfNeeded(file);
      await appendFile(file, `${JSON.stringify(event)}\n`, "utf8");
    },
    async read() {
      try {
        const text = await readFile(file, "utf8");
        const kept: RuntimeEventV2[] = [];
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

async function compactOldRuns(runsRoot: string): Promise<void> {
  const cutoff = Date.now() - TRACE_RETENTION_MS;
  let entries: string[] = [];
  try {
    entries = await readdir(runsRoot);
  } catch {
    return;
  }
  for (const entry of entries) {
    const dir = join(runsRoot, entry);
    try {
      const info = await stat(dir);
      if (!info.isDirectory() || info.mtimeMs >= cutoff) continue;
      await rm(dir, { recursive: true, force: true });
    } catch {
      // Leave a directory that cannot be removed.
    }
  }
}

async function rotateIfNeeded(file: string): Promise<void> {
  try {
    const info = await stat(file);
    if (info.size < TRACE_ROTATE_BYTES) return;
    await rename(file, `${file}.${Date.now()}`);
  } catch {
    // The file does not exist yet.
  }
}
