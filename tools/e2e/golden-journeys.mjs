#!/usr/bin/env node
/**
 * Golden lower-layer runner — runner-safe Node spawn of Vitest (no nested tsx).
 * Journey 01 (headed) is separate via npm run test:e2e:msrp.
 * Journey 06 has no harness yet; its absence from the vitest set is intentional
 * and enforced by the golden manifest architecture test.
 */
import { spawn } from "node:child_process";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  assertGoldenJourneyManifestIntegrity,
  goldenJourneys,
  integrationVitestFiles,
} from "./golden-journeys.manifest.mjs";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const integrity = assertGoldenJourneyManifestIntegrity();
if (!integrity.ok) {
  console.error("golden journey manifest invalid:");
  for (const error of integrity.errors) console.error(`- ${error}`);
  process.exit(1);
}

console.log("Golden journeys (canonical 01–12)");
for (const journey of goldenJourneys) {
  const harness = journey.harness ?? "(none)";
  console.log(`- ${journey.id} [${journey.proofKind}] ${journey.title} → ${harness}`);
}

const files = integrationVitestFiles();
if (files.length === 0) {
  console.error("no integration-lower-layer vitest files mapped");
  process.exit(1);
}

console.log("");
console.log(`Running vitest integration on ${files.length} suite file(s)…`);

const npmCmd = process.platform === "win32" ? "npm.cmd" : "npm";
const code = await new Promise((resolveExit) => {
  const child = spawn(
    npmCmd,
    ["exec", "--", "vitest", "run", "--project", "integration", ...files],
    { cwd: ROOT, stdio: "inherit", env: process.env, shell: process.platform === "win32" },
  );
  child.on("error", (error) => {
    console.error(error);
    resolveExit(1);
  });
  child.on("exit", (exitCode, signal) => {
    if (signal) {
      console.error(`vitest terminated by signal ${signal}`);
      resolveExit(1);
      return;
    }
    resolveExit(exitCode ?? 1);
  });
});

if (code !== 0) {
  console.error(`golden journeys failed with exit ${code}`);
  process.exit(code);
}
console.log("ok golden journeys (integration-lower-layer only; not V1 product proof)");
