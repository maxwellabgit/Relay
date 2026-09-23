#!/usr/bin/env node
/**
 * Reports Expo/Apple release identifiers that are still placeholders.
 * Does not invent an account, team, or project id.
 */
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const appConfig = readFileSync(resolve(root, "apps/relay/app.config.ts"), "utf8");
const eas = JSON.parse(readFileSync(resolve(root, "apps/relay/eas.json"), "utf8"));
const submit = eas?.submit?.production?.ios ?? {};

const missing = [];
const projectMatch = appConfig.match(/projectId:\s*"([^"]+)"/);
const projectId = projectMatch?.[1] ?? "";
if (!projectId || projectId === "00000000-0000-0000-0000-000000000000") {
  missing.push({
    field: "extra.eas.projectId",
    file: "apps/relay/app.config.ts",
    reason: "placeholder project id; do not reuse another app's EAS project",
  });
}
for (const field of ["appleId", "ascAppId", "appleTeamId"]) {
  const value = submit[field];
  if (typeof value !== "string" || value.startsWith("REPLACE_WITH_")) {
    missing.push({
      field: `submit.production.ios.${field}`,
      file: "apps/relay/eas.json",
      reason: "account-owned value is not in the repository",
    });
  }
}

const report = {
  status: missing.length === 0 ? "PASS" : "HUMAN_BLOCKED",
  present: {
    iosBundleIdentifier: "app.relay.assistant",
    androidPackage: "app.relay.assistant",
    marketingVersion: "1.0.0",
    slug: "relay-assistant",
  },
  missing,
};
console.log(JSON.stringify(report, null, 2));
process.exit(missing.length === 0 ? 0 : 1);
