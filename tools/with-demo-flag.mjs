#!/usr/bin/env node
/**
 * Runs a child command with EXPO_PUBLIC_RELAY_ALLOW_DEMO=1 for intentional browser demos.
 * Usage: node tools/with-demo-flag.mjs <command> [...args]
 */
import { spawn } from "node:child_process";

const args = process.argv.slice(2);
if (args.length === 0) {
  console.error("usage: node tools/with-demo-flag.mjs <command> [...args]");
  process.exit(2);
}

const [command, ...rest] = args;
const child = spawn(command, rest, {
  stdio: "inherit",
  shell: process.platform === "win32",
  env: {
    ...process.env,
    EXPO_PUBLIC_RELAY_ALLOW_DEMO: "1",
  },
});
child.on("exit", (code, signal) => {
  if (signal) process.exit(1);
  process.exit(code ?? 1);
});
