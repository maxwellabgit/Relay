#!/usr/bin/env node
/**
 * RELAY V1 verification gate. Streams each step's output live, stops on first
 * failure, and always writes a sanitized summary under .dev-data/verify/.
 * Step list comes from tools/verification/manifest.mjs.
 */
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { verificationManifest } from "./verification/manifest.mjs";

const steps = verificationManifest.steps;
const started = new Date().toISOString();
const gateStartedMs = Date.now();
const results = [];

console.log(`verify:v1 starting (${steps.length} steps) at ${started}`);

for (let index = 0; index < steps.length; index += 1) {
  const step = steps[index];
  const stepLabel = `[${index + 1}/${steps.length}] ${step.name}`;
  const began = Date.now();
  console.log(`→ ${stepLabel}`);

  const entry = await runStep(step);
  entry.elapsedMs = Date.now() - began;
  results.push(entry);

  if (!entry.ok) {
    writeSummary({
      started,
      finished: new Date().toISOString(),
      ok: false,
      failedStep: step.name,
      totalElapsedMs: Date.now() - gateStartedMs,
      results,
    });
    console.error(`verify:v1 failed at ${step.name} after ${entry.elapsedMs}ms`);
    process.exit(entry.status === null ? 1 : entry.status);
  }
  console.log(`ok ${step.name} (${entry.elapsedMs}ms, total ${Date.now() - gateStartedMs}ms)`);
}

writeSummary({
  started,
  finished: new Date().toISOString(),
  ok: true,
  failedStep: null,
  totalElapsedMs: Date.now() - gateStartedMs,
  results,
});
console.log(`verify:v1 passed in ${Date.now() - gateStartedMs}ms`);

/**
 * @param {{ name: string; command: string; args?: string[]; cwd?: string; env?: Record<string, string> }} step
 */
function runStep(step) {
  return new Promise((resolveExit) => {
    const child = spawn(step.command, step.args ?? [], {
      cwd: step.cwd ? resolve(process.cwd(), step.cwd) : process.cwd(),
      env: { ...process.env, ...(step.env ?? {}) },
      shell: process.platform === "win32",
      stdio: ["ignore", "inherit", "inherit"],
    });
    child.on("error", (error) => {
      console.error(error);
      resolveExit({ name: step.name, ok: false, status: 1 });
    });
    child.on("exit", (code, signal) => {
      if (signal) {
        console.error(`step ${step.name} terminated by signal ${signal}`);
        resolveExit({ name: step.name, ok: false, status: 1 });
        return;
      }
      resolveExit({ name: step.name, ok: code === 0, status: code });
    });
  });
}

function writeSummary(summary) {
  const dir = resolve(process.cwd(), ".dev-data", "verify");
  mkdirSync(dir, { recursive: true });
  writeFileSync(
    resolve(dir, "latest-summary.json"),
    `${JSON.stringify(summary, null, 2)}\n`,
    "utf8",
  );
}
