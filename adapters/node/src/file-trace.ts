import { appendFile, mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { DiagnosticLatestPointer, DiagnosticLiveSummary } from "@relay/contracts";
import { TRACE_RETENTION_MS, TRACE_ROTATE_BYTES } from "@relay/engine";
import { isTraceEvent, type TraceSink } from "@relay/engine";
import type { RuntimeEventV2 } from "@relay/engine";
import { observeDiagnostics, type DiagnosticObservation } from "@relay/engine";
import { createDiagnosticPathPort } from "./diagnostic-paths.js";

export type FileTraceOptions = {
  readonly runsRoot?: string;
  readonly diagnosticsRoot?: string;
  readonly runId?: string;
  readonly commit?: string;
  readonly profile?: string;
  readonly mode?: DiagnosticLatestPointer["mode"];
};

export function createFileTraceSink(
  runsRootOrOptions: string | FileTraceOptions = "",
  runIdArg = `run_${Date.now().toString(36)}`,
): TraceSink {
  const options: FileTraceOptions =
    typeof runsRootOrOptions === "string"
      ? {
          ...(runsRootOrOptions ? { runsRoot: runsRootOrOptions } : {}),
          runId: runIdArg,
        }
      : runsRootOrOptions;
  const paths = createDiagnosticPathPort(
    {
      ...process.env,
      ...(options.diagnosticsRoot
        ? { RELAY_DIAGNOSTICS_ROOT: options.diagnosticsRoot }
        : {}),
    },
    process.cwd(),
  );
  const runsRoot = options.runsRoot ?? paths.runsRoot();
  const runId = options.runId ?? runIdArg;
  const directory = join(runsRoot, runId);
  const file = join(directory, "events.jsonl");
  const startedAt = new Date().toISOString();
  const commit = options.commit ?? "test";
  const profile = options.profile ?? "node-harness";
  const mode = options.mode ?? "test";
  const canonicalRuns = paths.runsRoot().replace(/[/\\]+$/, "").toLowerCase();
  const effectiveRuns = runsRoot.replace(/[/\\]+$/, "").toLowerCase();
  const publishLatest =
    options.diagnosticsRoot != null ||
    effectiveRuns === canonicalRuns ||
    effectiveRuns.startsWith(`${canonicalRuns}${canonicalRuns.includes("\\") ? "\\" : "/"}`) ||
    /[/\\]diagnostics[/\\]runs(?:[/\\]|$)/i.test(runsRoot);
  let wroteManifest = false;
  const observations: DiagnosticObservation[] = [];

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
              applicationVersion: "0.1.0",
              gitCommit: commit,
              protocolVersion: "2",
              reflexVersions: { "resolve-acronym": 1 },
              policyVersions: { "resolve-acronym@1": "v1" },
              providerModes: ["recorded"],
              startedAt,
              status: "running",
              uncleanShutdown: false,
            },
            null,
            2,
          )}\n`,
          "utf8",
        );
        wroteManifest = true;
        if (publishLatest) {
          await writeLatestAndHeartbeat({
            paths,
            runId,
            runDir: directory,
            commit,
            profile,
            mode,
            startedAt,
            cleanShutdown: null,
            uncleanShutdown: false,
          });
        }
        await writeLiveSummary({ paths, runId, runDir: directory, commit, profile, observations });
      }
      observations.push(observationFrom(event));
      if (observations.length > 1000) observations.splice(0, observations.length - 1000);
      await compactOldRuns(runsRoot);
      await rotateIfNeeded(file);
      await appendFile(file, `${JSON.stringify(event)}\n`, "utf8");
      if (publishLatest) {
        await writeLatestAndHeartbeat({
          paths,
          runId,
          runDir: directory,
          commit,
          profile,
          mode,
          startedAt,
          cleanShutdown: null,
          uncleanShutdown: false,
        });
      }
      await writeLiveSummary({ paths, runId, runDir: directory, commit, profile, observations });
      if (event.eventType === "run.ended") {
        await completeManifest(directory, true);
        if (publishLatest) {
          await writeLatestAndHeartbeat({
            paths,
            runId,
            runDir: directory,
            commit,
            profile,
            mode,
            startedAt,
            cleanShutdown: true,
            uncleanShutdown: false,
          });
        }
      }
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

async function writeLatestAndHeartbeat(input: {
  paths: ReturnType<typeof createDiagnosticPathPort>;
  runId: string;
  runDir: string;
  commit: string;
  profile: string;
  mode: DiagnosticLatestPointer["mode"];
  startedAt: string;
  cleanShutdown: boolean | null;
  uncleanShutdown: boolean;
}): Promise<void> {
  await mkdir(input.paths.diagnosticsRoot(), { recursive: true });
  const now = new Date().toISOString();
  const latest: DiagnosticLatestPointer = {
    schemaVersion: 1,
    runId: input.runId,
    runDir: input.runDir,
    commit: input.commit,
    profile: input.profile,
    mode: input.mode,
    startedAt: input.startedAt,
    heartbeatAt: now,
    pid: process.pid,
    cleanShutdown: input.cleanShutdown,
    uncleanShutdown: input.uncleanShutdown,
  };
  const tmp = `${input.paths.latestPointerPath()}.tmp`;
  await writeFile(tmp, `${JSON.stringify(latest, null, 2)}\n`, "utf8");
  await rename(tmp, input.paths.latestPointerPath());
  await writeFile(
    join(input.runDir, "heartbeat.json"),
    `${JSON.stringify(
      {
        schemaVersion: 1,
        runId: input.runId,
        at: now,
        pid: process.pid,
        status: input.cleanShutdown === true ? "completed" : "running",
      },
      null,
      2,
    )}\n`,
    "utf8",
  );
}

function observationFrom(event: RuntimeEventV2): DiagnosticObservation {
  return {
    at: event.at,
    eventType: event.eventType,
    status: event.status,
    stage: event.stage,
    ...(event.reasonCode ? { reasonCode: event.reasonCode } : {}),
    ...(event.queueDepth != null ? { queueDepth: event.queueDepth } : {}),
    ...(event.caseId ? { caseId: event.caseId } : {}),
    ...(event.attempt != null ? { attempt: event.attempt } : {}),
    ...(event.reflexId ? { reflexId: event.reflexId } : {}),
    ...(event.toolId ? { toolId: event.toolId } : {}),
  };
}

async function writeLiveSummary(input: {
  paths: ReturnType<typeof createDiagnosticPathPort>;
  runId: string;
  runDir: string;
  commit: string;
  profile: string;
  observations: readonly DiagnosticObservation[];
}): Promise<void> {
  const observed = observeDiagnostics(input.observations, Date.now());
  const summary: DiagnosticLiveSummary = {
    schemaVersion: 1,
    runId: input.runId,
    commit: input.commit,
    profile: input.profile,
    providerReadiness: observed.providerReadiness,
    queue: {
      ready: observed.queueReady,
      oldestReadyMs: observed.oldestReadyMs,
      deadLetters: observed.deadLetters,
    },
    activeCases: observed.activeCases,
    waitingCases: observed.waitingCases,
    failedCases: observed.failedCases,
    latestCase: observed.latestCase,
    latestJudgment: observed.latestJudgment,
    latestTool: observed.latestTool,
    deadLetters: observed.deadLetters,
    retrySchedule: observed.retrySchedule,
    paths: {
      runDir: input.runDir,
      eventsPath: join(input.runDir, "events.jsonl"),
      liveSummaryPath: join(input.runDir, "live-summary.json"),
      latestPointerPath: input.paths.latestPointerPath(),
    },
    updatedAt: new Date().toISOString(),
  };
  await writeFile(join(input.runDir, "live-summary.json"), `${JSON.stringify(summary, null, 2)}\n`, "utf8");
}

async function completeManifest(directory: string, clean: boolean): Promise<void> {
  const path = join(directory, "manifest.json");
  try {
    const text = await readFile(path, "utf8");
    const manifest = JSON.parse(text) as {
      status?: string;
      startedAt?: string;
      endedAt?: string;
      uncleanShutdown?: boolean;
    };
    if (manifest.status !== "running") return;
    await writeFile(
      path,
      `${JSON.stringify(
        {
          ...manifest,
          status: clean ? "completed" : "unclean_shutdown",
          endedAt: new Date().toISOString(),
          uncleanShutdown: !clean,
        },
        null,
        2,
      )}\n`,
      "utf8",
    );
  } catch {
    // Ignore missing manifest.
  }
}

async function rotateIfNeeded(file: string): Promise<void> {
  try {
    const info = await stat(file);
    if (info.size <= TRACE_ROTATE_BYTES) return;
    await rename(file, `${file}.${Date.now()}.rotated`);
  } catch {
    // File may not exist yet.
  }
}

async function compactOldRuns(runsRoot: string): Promise<void> {
  try {
    const entries = await readdir(runsRoot, { withFileTypes: true });
    const cutoff = Date.now() - TRACE_RETENTION_MS;
    for (const entry of entries) {
      if (!entry.isDirectory() || !entry.name.startsWith("run_")) continue;
      const full = join(runsRoot, entry.name);
      const info = await stat(full);
      if (info.mtimeMs < cutoff) await rm(full, { recursive: true, force: true });
    }
  } catch {
    // Root may not exist yet.
  }
}
