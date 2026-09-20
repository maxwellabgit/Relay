#!/usr/bin/env node
/**
 * RELAY V1 verification gate. Stops on first failure and writes a sanitized summary.
 */
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const steps = [
  { name: "format", command: "npm", args: ["run", "format:check"] },
  { name: "lint", command: "npm", args: ["run", "lint"] },
  { name: "typecheck", command: "npm", args: ["run", "typecheck"] },
  { name: "unit", command: "npm", args: ["run", "test:unit"] },
  { name: "architecture", command: "npm", args: ["run", "test:architecture"] },
  { name: "integration", command: "npm", args: ["run", "test:integration"] },
  { name: "replay", command: "npm", args: ["run", "test:replay"] },
  { name: "privacy", command: "npm", args: ["run", "test:privacy"] },
  { name: "web-export", command: "npm", args: ["run", "build:web"] },
  { name: "ios-export", command: "npm", args: ["run", "export:ios", "--workspace", "@relay/app"] },
  { name: "halo", command: "npm", args: ["run", "halo:test"] },
  { name: "cargo-fmt", command: "cargo", args: ["fmt", "--check"], cwd: "apps/desktop/src-tauri" },
  {
    name: "cargo-clippy",
    command: "cargo",
    args: ["clippy", "--", "-D", "warnings"],
    cwd: "apps/desktop/src-tauri",
  },
  { name: "cargo-test", command: "cargo", args: ["test"], cwd: "apps/desktop/src-tauri" },
  { name: "smoke", command: "npm", args: ["run", "test:smoke"] },
  { name: "desktop-build", command: "npm", args: ["run", "build:desktop"], env: { CI: "true" } },
];

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
