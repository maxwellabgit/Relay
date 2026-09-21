import { join } from "node:path";
import type { DiagnosticPathPort } from "@relay/contracts";
import { DIAGNOSTICS_ROOT_ENV } from "@relay/contracts";

/**
 * Resolve the canonical diagnostics root shared by desktop, Node, CLI, and tests.
 * Priority: RELAY_DIAGNOSTICS_ROOT → LOCALAPPDATA/RELAY/diagnostics → ./RELAY/diagnostics
 */
export function resolveDiagnosticsRoot(
  env: NodeJS.ProcessEnv = process.env,
  cwd: string = process.cwd(),
): string {
  const override = env[DIAGNOSTICS_ROOT_ENV]?.trim();
  if (override) return override;
  const local = env.LOCALAPPDATA?.trim();
  if (local) return join(local, "RELAY", "diagnostics");
  return join(cwd, "RELAY", "diagnostics");
}

export function createDiagnosticPathPort(
  env: NodeJS.ProcessEnv = process.env,
  cwd: string = process.cwd(),
): DiagnosticPathPort {
  const root = resolveDiagnosticsRoot(env, cwd);
  const runs = join(root, "runs");
  return {
    diagnosticsRoot: () => root,
    runsRoot: () => runs,
    runDir: (runId) => join(runs, runId),
    latestPointerPath: () => join(root, "latest.json"),
    liveSummaryPath: (runId) => join(runs, runId, "live-summary.json"),
    heartbeatPath: (runId) => join(runs, runId, "heartbeat.json"),
    eventsPath: (runId) => join(runs, runId, "events.jsonl"),
  };
}
