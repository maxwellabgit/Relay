import { describe, expect, it } from "vitest";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { RunDiagnostics } from "./diagnostics.js";

const SENTINEL = "PRIVACY_SENTINEL_7f3a9c2e";

describe("privacy boundary", () => {
  it("keeps privacy sentinel out of events.jsonl and only in protected artifacts", () => {
    const root = mkdtempSync(join(tmpdir(), "relay-privacy-"));
    try {
      const diag = new RunDiagnostics({
        runsRoot: root,
        appVersion: "0.1.0",
        protocolVersion: "1",
      });
      diag.writeManifest();
      mkdirSync(join(diag.runDir, "artifacts"), { recursive: true });
      writeFileSync(join(diag.runDir, "artifacts", "request.json"), JSON.stringify({ text: SENTINEL }), "utf8");
      diag.append({
        type: "judgment.completed",
        judgmentId: "jud_1",
        artifactRef: "artifacts/request.json",
        reasonCode: "recorded",
      });
      const events = readFileSync(join(diag.runDir, "events.jsonl"), "utf8");
      expect(events).not.toContain(SENTINEL);
      expect(readFileSync(join(diag.runDir, "artifacts", "request.json"), "utf8")).toContain(SENTINEL);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
