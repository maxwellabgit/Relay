#!/usr/bin/env node
/**
 * Live diagnostics CLI — resolves latest.json under the canonical diagnostics root.
 */
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { createDiagnosticPathPort } from "@relay/adapter-node";
import type { DiagnosticLatestPointer, DiagnosticLiveSummary } from "@relay/contracts";

const args = process.argv.slice(2);
const command = args[0] ?? "help";

function flag(name: string): string | undefined {
  const idx = args.indexOf(name);
  if (idx < 0) return undefined;
  return args[idx + 1];
}

function paths() {
  return createDiagnosticPathPort();
}

function readLatest(): DiagnosticLatestPointer {
  const pointer = paths().latestPointerPath();
  if (!existsSync(pointer)) {
    throw new Error(
      `No latest.json at ${pointer}. Is RELAY running, or set RELAY_DIAGNOSTICS_ROOT?`,
    );
  }
  return JSON.parse(readFileSync(pointer, "utf8")) as DiagnosticLatestPointer;
}

function diagnoseLatest(): void {
  const latest = readLatest();
  const summaryPath = join(latest.runDir, "live-summary.json");
  const summary = existsSync(summaryPath)
    ? (JSON.parse(readFileSync(summaryPath, "utf8")) as DiagnosticLiveSummary)
    : null;
  console.log(
    JSON.stringify(
      {
        latest,
        liveSummary: summary,
        eventsPath: join(latest.runDir, "events.jsonl"),
        heartbeatPath: join(latest.runDir, "heartbeat.json"),
      },
      null,
      2,
    ),
  );
}

function diagnoseCase(): void {
  const caseId = flag("--case") ?? args[1];
  if (!caseId) {
    console.error("Usage: npm run diagnose:case -- --case <caseId>");
    process.exit(1);
  }
  const latest = readLatest();
  const eventsPath = join(latest.runDir, "events.jsonl");
  const lines = existsSync(eventsPath)
    ? readFileSync(eventsPath, "utf8")
        .split("\n")
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line) => JSON.parse(line) as { caseId?: string })
        .filter((row) => row.caseId === caseId)
    : [];
  const bundleDir = join(latest.runDir, "bundles", caseId);
  mkdirSync(bundleDir, { recursive: true });
  const bundlePath = join(bundleDir, "case-bundle.json");
  writeFileSync(
    bundlePath,
    `${JSON.stringify(
      {
        caseId,
        runId: latest.runId,
        runDir: latest.runDir,
        eventCount: lines.length,
        events: lines,
        note: "Privacy-safe structural bundle; ambient transcript prose is not included.",
      },
      null,
      2,
    )}\n`,
    "utf8",
  );
  console.log(JSON.stringify({ ok: true, caseId, bundlePath, eventCount: lines.length }, null, 2));
}

async function logsFollow(): Promise<void> {
  const latest = readLatest();
  const eventsPath = join(latest.runDir, "events.jsonl");
  console.error(`Following ${eventsPath}`);
  if (!existsSync(eventsPath)) {
    console.error("events.jsonl not found yet; waiting…");
  }
  let offset = existsSync(eventsPath) ? readFileSync(eventsPath, "utf8").length : 0;
  for (;;) {
    if (existsSync(eventsPath)) {
      const text = readFileSync(eventsPath, "utf8");
      if (text.length > offset) {
        const chunk = text.slice(offset);
        offset = text.length;
        for (const line of chunk.split("\n")) {
          if (line.trim()) console.log(line);
        }
      }
    }
    await new Promise((r) => setTimeout(r, 250));
  }
}

function e2eLast(): void {
  const root = resolve(process.cwd(), ".dev-data", "e2e");
  if (!existsSync(root)) {
    console.error("No .dev-data/e2e artifacts yet");
    process.exit(1);
  }
  const preferred = join(root, "msrp-headed-latest", "result.json");
  if (existsSync(preferred)) {
    console.log(readFileSync(preferred, "utf8"));
    return;
  }
  const dirs = readdirSync(root).sort();
  const last = dirs.at(-1);
  if (!last) {
    console.error("No e2e results");
    process.exit(1);
  }
  const result = join(root, last, "result.json");
  if (!existsSync(result)) {
    console.error(`Missing ${result}`);
    process.exit(1);
  }
  console.log(readFileSync(result, "utf8"));
}

function compareRuns(): void {
  const a = flag("--a");
  const b = flag("--b");
  if (!a || !b) {
    console.error("Usage: npm run compare:runs -- --a <runDir> --b <runDir>");
    process.exit(1);
  }
  const readTypes = (dir: string) =>
    existsSync(join(dir, "events.jsonl"))
      ? readFileSync(join(dir, "events.jsonl"), "utf8")
          .split("\n")
          .map((line) => line.trim())
          .filter(Boolean)
          .map((line) => (JSON.parse(line) as { eventType?: string }).eventType ?? "?")
      : [];
  const left = readTypes(a);
  const right = readTypes(b);
  console.log(
    JSON.stringify(
      {
        a: { dir: a, events: left.length, types: left },
        b: { dir: b, events: right.length, types: right },
        typeDelta: {
          onlyA: left.filter((t) => !right.includes(t)),
          onlyB: right.filter((t) => !left.includes(t)),
        },
      },
      null,
      2,
    ),
  );
}

async function main(): Promise<void> {
  switch (command) {
    case "diagnose:latest":
    case "latest":
      diagnoseLatest();
      return;
    case "diagnose:case":
    case "case":
      diagnoseCase();
      return;
    case "logs:follow":
    case "follow":
      await logsFollow();
      return;
    case "e2e:last":
      e2eLast();
      return;
    case "compare:runs":
    case "compare":
      compareRuns();
      return;
    default:
      console.log(`Usage:
  npm run diagnose:latest
  npm run diagnose:case -- --case <caseId>
  npm run logs:follow
  npm run e2e:last
  npm run compare:runs -- --a <runDir> --b <runDir>`);
      process.exit(command === "help" ? 0 : 1);
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
