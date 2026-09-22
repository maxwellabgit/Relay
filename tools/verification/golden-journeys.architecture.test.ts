import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  assertGoldenJourneyManifestIntegrity,
  goldenJourneys,
  GOLDEN_JOURNEY_IDS,
  integrationVitestFiles,
} from "../e2e/golden-journeys.manifest.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

describe("golden journey manifest", () => {
  it("defines exactly journey numbers 01–12 with no duplicates", () => {
    const integrity = assertGoldenJourneyManifestIntegrity();
    expect(integrity.errors).toEqual([]);
    expect(integrity.ok).toBe(true);
    expect(goldenJourneys.map((j) => j.number)).toEqual([...GOLDEN_JOURNEY_IDS]);
  });

  it("maps every non-missing journey to an existing harness file", () => {
    const missingFiles: string[] = [];
    for (const journey of goldenJourneys) {
      if (journey.proofKind === "missing-harness") {
        expect(journey.harness).toBeNull();
        continue;
      }
      expect(journey.harness).toBeTruthy();
      const absolute = resolve(root, journey.harness!);
      if (!existsSync(absolute)) missingFiles.push(`${journey.id}:${journey.harness}`);
    }
    expect(missingFiles).toEqual([]);
  });

  it("refuses to treat integration-only or missing journeys as headed-product", () => {
    const headed = goldenJourneys.filter((j) => j.proofKind === "headed-product");
    expect(headed.map((j) => j.id)).toEqual(["01-deterministic-glossary"]);
    const missing = goldenJourneys.filter((j) => j.proofKind === "missing-harness");
    expect(missing.map((j) => j.id)).toEqual(["06-foreground-listening"]);
    expect(integrationVitestFiles().length).toBeGreaterThan(0);
  });

  it("package.json golden script uses runner-safe node (no nested tsx wrapper)", () => {
    const pkg = JSON.parse(readFileSync(resolve(root, "package.json"), "utf8")) as {
      scripts: Record<string, string>;
    };
    expect(pkg.scripts["test:e2e:golden"]).toBe("node tools/e2e/golden-journeys.mjs");
    expect(pkg.scripts["test:e2e:golden"]).not.toMatch(/\btsx\b/);
  });
});

describe("release truth enforcement", () => {
  it("CI job has a 45-minute timeout and uploads verify summary", () => {
    const workflow = readFileSync(resolve(root, ".github/workflows/check.yml"), "utf8");
    expect(workflow).toMatch(/timeout-minutes:\s*45/);
    expect(workflow).toContain("verify-v1-summary");
    expect(workflow).toContain(".dev-data/verify/latest-summary.json");
  });

  it("production-core status is foundation-complete / V1 blocked, not V1 complete", () => {
    const status = readFileSync(
      resolve(root, "docs/implementation/PRODUCTION_CORE_STATUS.md"),
      "utf8",
    );
    expect(status).toMatch(/FOUNDATION COMPLETE \/ V1 BLOCKED/);
    expect(status).not.toMatch(/\|\s*Active phase\s*\|\s*COMPLETE \(Phases 0–8 GREEN\)\s*\|/);
    expect(status).toContain("35727102376");
    expect(status).toContain("4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6");
  });

  it("finalization ledger exists and requires exact-SHA greens", () => {
    const ledger = readFileSync(
      resolve(root, "docs/implementation/FINALIZATION_STATUS.md"),
      "utf8",
    );
    expect(ledger).toMatch(/Exact-SHA evidence is mandatory/i);
    expect(ledger).toMatch(/NO-GO \/ V1 BLOCKED/);
    expect(ledger).toContain("head_sha");
  });

  it("release authority review document is present", () => {
    expect(
      existsSync(
        resolve(root, "docs/implementation/RELAY_V1_TestFlight_Finalization_Review_4b64928.md"),
      ),
    ).toBe(true);
  });
});
