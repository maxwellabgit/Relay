#!/usr/bin/env node
/* global console, process */
/**
 * Device model and speech tournament.
 * Does not invent results. Exits 2 when the named phones are not attached.
 */
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const required = ["iPhone 15 Pro Max", "Galaxy S23 Ultra"];
const adb = spawnSync("adb", ["devices"], { encoding: "utf8" });
const ios = spawnSync("idevice_id", ["-l"], { encoding: "utf8" });
const adbOut = adb.stdout ?? "";
const androidAttached = adb.status === 0 && /device$/.test(adbOut.split("\n").slice(1).join("\n"));
const iosAttached = ios.status === 0 && (ios.stdout ?? "").trim().length > 0;

const report = {
  status: androidAttached && iosAttached ? "NOT_RUN" : "DEVICE_BLOCKED",
  required,
  androidAttached,
  iosAttached,
  adb: adb.error ? "adb_missing" : adbOut.trim(),
  reason:
    "Attach the iPhone 15 Pro Max and the Galaxy S23 Ultra, then rerun this command. Do not record a model winner without those measurements.",
  resume: "npm run test:device:tournament",
  measure: [
    "download size",
    "peak memory",
    "cold load",
    "warm load",
    "time to first token",
    "tokens per second",
    "battery and thermal notes",
    "fixed prompt quality",
    "crash and recovery",
  ],
};
const dir = resolve(root, ".dev-data", "device");
mkdirSync(dir, { recursive: true });
writeFileSync(resolve(dir, "model-tournament.json"), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
process.exit(report.status === "DEVICE_BLOCKED" ? 2 : 0);
