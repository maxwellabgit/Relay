#!/usr/bin/env node
/**
 * RELAY V1 verification gate. Stops on first failure and writes a sanitized summary.
 * Step list comes from tools/verification/manifest.mjs (shared with GitHub Actions).
 */
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { verificationManifest } from "./verification/manifest.mjs";

const steps = verificationManifest.steps;
const started = new Date().toISOString();
const results = [];

for (const step of steps) {
  const began = Date.now();
  const result = spawnSync(step.command, step.args, {
    cwd: step.cwd ? resolve(process.cwd(), step.cwd) : process.cwd(),
    env: { ...process.env, ...(step.env ?? {}) },
    encoding: "utf8",
    shell: process.platform === "win32",
  });
  const entry = {
    name: step.name,
    ok: result.status === 0,
    elapsedMs: Date.now() - began,
    status: result.status,
  };
  results.push(entry);
  if (!entry.ok) {
    writeSummary({
      started,
      finished: new Date().toISOString(),
      ok: false,
      failedStep: step.name,
      results,
    });
    console.error(`verify:v1 failed at ${step.name}`);
    process.exit(result.status === null ? 1 : result.status);
  }
  console.log(`ok ${step.name} (${entry.elapsedMs}ms)`);
}

writeSummary({ started, finished: new Date().toISOString(), ok: true, failedStep: null, results });
console.log("verify:v1 passed");

function writeSummary(summary) {
  const dir = resolve(process.cwd(), ".dev-data", "verify");
  mkdirSync(dir, { recursive: true });
  writeFileSync(
    resolve(dir, "latest-summary.json"),
    `${JSON.stringify(summary, null, 2)}\n`,
    "utf8",
  );
}
