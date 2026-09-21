import { mkdtemp, readFile, rm } from "node:fs/promises";
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
      };
      expect(latest.runId).toBe("run_test1");
      expect(latest.uncleanShutdown).toBe(false);
      const summary = JSON.parse(await readFile(join(runsRoot, "run_test1", "live-summary.json"), "utf8")) as {
        latestJudgment: { observed: boolean };
      };
      expect(summary.latestJudgment.observed).toBe(false);
      const events = await readFile(join(runsRoot, "run_test1", "events.jsonl"), "utf8");
      expect(events).toContain("source.accepted");
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
});
