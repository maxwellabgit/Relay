/**
 * Release readiness is not "the runner finished".
 * Every golden journey must be PASS in headed evidence. Blocked and NOT_RUN fail the gate.
 */
import { readFileSync, existsSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { goldenJourneys } from "../e2e/golden-journeys.manifest.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const evidencePaths = [
  resolve(root, ".dev-data/e2e/desktop-product-latest/result.json"),
  resolve(root, ".dev-data/e2e/msrp-headed-latest/result.json"),
];

const rows = [];
let runnerCompleted = false;
for (const path of evidencePaths) {
  if (!existsSync(path)) continue;
  const parsed = JSON.parse(readFileSync(path, "utf8"));
  if (parsed.runnerCompleted === true) runnerCompleted = true;
  if (Array.isArray(parsed.journeys)) rows.push(...parsed.journeys);
}

const missing = [];
const blocked = [];
for (const journey of goldenJourneys) {
  const found = rows.filter((row) => row.id === journey.id);
  const passed = found.some((row) => row.status === "PASS");
  if (!passed) {
    const status = found[0]?.status ?? "NOT_RUN";
    missing.push(`${journey.id}:${status}`);
    if (status !== "FAIL") blocked.push(`${journey.id}:${status}`);
  }
}

const releaseJourneysPassed = missing.length === 0;
const report = {
  runnerCompleted,
  releaseJourneysPassed,
  ok: releaseJourneysPassed,
  missing,
};
console.log(JSON.stringify(report, null, 2));
if (!releaseJourneysPassed) process.exit(1);
