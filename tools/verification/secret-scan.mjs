#!/usr/bin/env node
/**
 * Fails if source looks like it contains a private key or a live provider token.
 * Placeholders such as REPLACE_WITH_ are not secrets.
 */
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const skip = new Set([
  "node_modules",
  "dist",
  "target",
  ".git",
  "artifacts",
  ".dev-data",
  "coverage",
]);
const patterns = [
  { name: "pem-private-key", re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/ },
  { name: "aws-access-key", re: /AKIA[0-9A-Z]{16}/ },
  { name: "openai-style-key", re: /sk-[A-Za-z0-9]{20,}/ },
];
const hits = [];

function walk(dir) {
  for (const name of readdirSync(dir)) {
    if (skip.has(name)) continue;
    const path = join(dir, name);
    const stat = statSync(path);
    if (stat.isDirectory()) {
      walk(path);
      continue;
    }
    if (!/\.(ts|tsx|js|mjs|json|rs|py|yml|yaml|toml|ps1|md)$/.test(name)) continue;
    if (stat.size > 1_000_000) continue;
    const text = readFileSync(path, "utf8");
    for (const pattern of patterns) {
      if (pattern.re.test(text)) {
        hits.push({ file: relative(root, path), rule: pattern.name });
      }
    }
  }
}

walk(root);
if (hits.length > 0) {
  console.error(JSON.stringify({ status: "FAIL", hits }, null, 2));
  process.exit(1);
}
console.log(JSON.stringify({ status: "PASS", filesScanned: "workspace-source", hits: [] }));
