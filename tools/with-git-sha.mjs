#!/usr/bin/env node
/**
 * Ensure EXPO_PUBLIC_GIT_SHA and GIT_COMMIT agree for frontend + native builds.
 * Usage: node tools/with-git-sha.mjs <command> [...args]
 */
/* eslint-env node */
import { spawn, execSync } from "node:child_process";

function resolveSha() {
  for (const key of ["GIT_COMMIT", "EXPO_PUBLIC_GIT_SHA", "GITHUB_SHA"]) {
    const value = process.env[key]?.trim();
    if (value && /^[0-9a-f]{7,40}$/i.test(value)) {
      return value.toLowerCase();
    }
  }
  try {
    const sha = execSync("git rev-parse HEAD", { encoding: "utf8" }).trim();
    if (/^[0-9a-f]{7,40}$/i.test(sha)) return sha.toLowerCase();
  } catch {
    // fall through
  }
  return "unknown";
}

const sha = resolveSha();
process.env.GIT_COMMIT = sha;
process.env.EXPO_PUBLIC_GIT_SHA = sha;

const [command, ...args] = process.argv.slice(2);
if (!command) {
  console.error("usage: node tools/with-git-sha.mjs <command> [...args]");
  process.exit(1);
}

const child = spawn(command, args, {
  stdio: "inherit",
  shell: process.platform === "win32",
  env: process.env,
});
child.on("exit", (code, signal) => {
  if (signal) process.kill(process.pid, signal);
  process.exit(code ?? 1);
});
