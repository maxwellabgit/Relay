import { describe, expect, it } from "vitest";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { evalThresholdDecision, RunDiagnostics } from "./diagnostics.js";

describe("diagnostics", () => {
  it("writes manifest and redacted events.jsonl without raw transcript", () => {
    const root = mkdtempSync(join(tmpdir(), "relay-runs-"));
    try {
      const diag = new RunDiagnostics({
        runsRoot: root,
        appVersion: "0.1.0",
        protocolVersion: "1",
      });
      diag.writeManifest();
      diag.append({
        type: "reflex.finding",
        caseId: "case_1",
        reflexId: "reflex.resolve-acronym",
        reflexVersion: 1,
        reasonCode: "exact_global",
        probabilities: { yes: 0.8 },
      });
      diag.end();
      const events = readFileSync(join(diag.runDir, "events.jsonl"), "utf8");
      expect(events).toContain("reflex.finding");
      expect(events.toLowerCase()).not.toContain("application programming");
      expect(readFileSync(join(diag.runDir, "manifest.json"), "utf8")).toContain("gitCommit");
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it("re-evaluates thresholds over stored probabilities", () => {
    const result = evalThresholdDecision({
      probabilities: { A: 0.7, no_match: 0.3 },
      usefulYes: 0.5,
      selected: "A",
      policy: {
        choiceProbabilityMinimum: 0.65,
        choiceMarginMinimum: 0.15,
        displayUsefulnessMinimum: 0.7,
      },
    });
    expect(result.show).toBe(false);
    expect(result.reasonCode).toBe("below_usefulness");
  });
});
