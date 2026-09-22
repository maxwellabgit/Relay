import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createFileTraceSink } from "./file-trace.js";
import { createDiagnosticPathPort, resolveDiagnosticsRoot } from "./diagnostic-paths.js";

describe("diagnostic paths", () => {
  it("resolves RELAY_DIAGNOSTICS_ROOT override first", () => {
    const root = resolveDiagnosticsRoot(
      { RELAY_DIAGNOSTICS_ROOT: "C:\\tmp\\relay-diag", LOCALAPPDATA: "C:\\Users\\x\\AppData\\Local" },
      "C:\\repo",
    );
    expect(root.replace(/\//g, "\\")).toBe("C:\\tmp\\relay-diag");
  });

  it("writes latest.json and live-summary.json beside events", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-diag-"));
    const diagnosticsRoot = join(root, "diagnostics");
    const runsRoot = join(diagnosticsRoot, "runs");
    const sink = createFileTraceSink({
      runsRoot,
      diagnosticsRoot,
      runId: "run_test1",
      commit: "abc",
      profile: "unit",
      mode: "test",
    });
    try {
      await sink.append({
        schemaVersion: 2,
        sequence: 1,
        runId: "run_test1",
        at: new Date().toISOString(),
        eventType: "source.accepted",
        stage: "source.accept",
        status: "completed",
      });
      const paths = createDiagnosticPathPort({ RELAY_DIAGNOSTICS_ROOT: diagnosticsRoot });
      const latest = JSON.parse(await readFile(paths.latestPointerPath(), "utf8")) as {
        runId: string;
        uncleanShutdown: boolean;
        startedAt: string;
      };
      expect(latest.runId).toBe("run_test1");
      expect(latest.uncleanShutdown).toBe(false);
      const firstStartedAt = latest.startedAt;
      await new Promise((resolve) => setTimeout(resolve, 20));
      await sink.append({
        schemaVersion: 2,
        sequence: 2,
        runId: "run_test1",
        at: new Date().toISOString(),
        eventType: "source.accepted",
        stage: "source.accept",
        status: "completed",
      });
      const latestAgain = JSON.parse(await readFile(paths.latestPointerPath(), "utf8")) as {
        startedAt: string;
        heartbeatAt: string;
      };
      expect(latestAgain.startedAt).toBe(firstStartedAt);
      expect(latestAgain.heartbeatAt >= firstStartedAt).toBe(true);
      const summary = JSON.parse(await readFile(join(runsRoot, "run_test1", "live-summary.json"), "utf8")) as {
        latestJudgment: { observed: boolean };
      };
      expect(summary.latestJudgment.observed).toBe(false);
      await sink.append({
        schemaVersion: 2,
        sequence: 3,
        runId: "run_test1",
        at: new Date(Date.now() - 5_000).toISOString(),
        eventType: "tool.completed",
        stage: "tool.execute",
        status: "completed",
        reasonCode: "completed",
        toolId: "assistant.respond",
        queueDepth: 1,
      });
      const derived = JSON.parse(await readFile(join(runsRoot, "run_test1", "live-summary.json"), "utf8")) as {
        latestTool: { toolId: string | null; observed: boolean };
        queue: { ready: number; oldestReadyMs: number };
      };
      expect(derived.latestTool).toEqual({ toolId: "assistant.respond", result: "completed", observed: true });
      expect(derived.queue.ready).toBe(1);
      expect(derived.queue.oldestReadyMs).toBeGreaterThanOrEqual(4_000);
      const events = await readFile(join(runsRoot, "run_test1", "events.jsonl"), "utf8");
      expect(events).toContain("source.accepted");
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it("does not overwrite canonical latest.json from an isolated harness runsRoot", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-harness-"));
    const diagnosticsRoot = join(root, "diagnostics");
    const harnessRuns = join(root, "tmp-runs");
    const paths = createDiagnosticPathPort({ RELAY_DIAGNOSTICS_ROOT: diagnosticsRoot });
    await mkdir(diagnosticsRoot, { recursive: true });
    const sentinel = {
      schemaVersion: 1,
      runId: "run_operator",
      runDir: join(diagnosticsRoot, "runs", "run_operator"),
      commit: "op",
      profile: "desktop",
      mode: "live",
      startedAt: "2026-01-01T00:00:00.000Z",
      heartbeatAt: "2026-01-01T00:00:00.000Z",
      pid: 1,
      cleanShutdown: null,
      uncleanShutdown: false,
    };
    await writeFile(paths.latestPointerPath(), `${JSON.stringify(sentinel)}\n`, "utf8");
    const prev = process.env.RELAY_DIAGNOSTICS_ROOT;
    process.env.RELAY_DIAGNOSTICS_ROOT = diagnosticsRoot;
    try {
      const sink = createFileTraceSink(harnessRuns, "run_harness");
      await sink.append({
        schemaVersion: 2,
        sequence: 1,
        runId: "run_harness",
        at: new Date().toISOString(),
        eventType: "source.accepted",
        stage: "source.accept",
        status: "completed",
      });
      const latest = JSON.parse(await readFile(paths.latestPointerPath(), "utf8")) as { runId: string };
      expect(latest.runId).toBe("run_operator");
    } finally {
      if (prev === undefined) delete process.env.RELAY_DIAGNOSTICS_ROOT;
      else process.env.RELAY_DIAGNOSTICS_ROOT = prev;
      await rm(root, { recursive: true, force: true });
    }
  });
});
