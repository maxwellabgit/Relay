#!/usr/bin/env node
import { createReadStream, existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createInterface } from "node:readline";
import { createNodeHarness } from "@relay/adapter-node";
import {
  evalThresholdDecision,
  latestRunDir,
  runReplay,
  RunDiagnostics,
} from "@relay/testkit";

const args = process.argv.slice(2);
const command = args[0] ?? "help";
const runsRoot = resolve("runs");

function flag(name: string): string | undefined {
  const idx = args.indexOf(name);
  if (idx < 0) return undefined;
  return args[idx + 1];
}

async function replay(): Promise<void> {
  const fixture = flag("--fixture") ?? args.find((a) => a.endsWith(".jsonl"));
  const speed = Number(flag("--speed") ?? "0");
  if (!fixture || Number.isNaN(speed)) {
    console.error("Usage: npm run replay -- --fixture <path> --speed <0|1|10>");
    process.exit(1);
  }
  const diag = new RunDiagnostics({
    runsRoot,
    appVersion: "0.1.0",
    protocolVersion: "1",
  });
  diag.writeManifest({ fixtureHashes: { [fixture]: "pending" } });
  const harness = createNodeHarness({ sessionId: "replay_session" });
  try {
    await harness.client.start();
    const result = await runReplay({
      fixturePath: resolve(fixture),
      speed,
      engine: harness.engine,
      sessionId: "replay_session",
    });
    await new Promise((r) => setTimeout(r, 150));
    const snap = await harness.client.getSnapshot();
    for (const item of snap.feedItems) {
      diag.append({
        type: item.kind === "finding" ? "reflex.finding" : "feed.item",
        reasonCode: item.kind,
        selectedOutcome: item.kind,
        ...(item.caseId ? { caseId: item.caseId } : {}),
      });
    }
    diag.writeSnapshot(snap);
    console.log(
      JSON.stringify(
        {
          runDir: diag.runDir,
          fixture,
          speed,
          finals: result.finals,
          events: result.events.length,
          sourceSegments: snap.sourceSegments.length,
          feedItems: snap.feedItems.length,
        },
        null,
        2,
      ),
    );
  } finally {
    await harness.client.stop();
    harness.close();
    diag.end();
  }
}

async function logsTail(): Promise<void> {
  const dir = latestRunDir(runsRoot);
  if (!dir) {
    console.error("No runs found under ./runs");
    process.exit(1);
  }
  const file = resolve(dir, "events.jsonl");
  console.error(`Tailing ${file}`);
  const stream = createReadStream(file, { encoding: "utf8" });
  const rl = createInterface({ input: stream, crlfDelay: Infinity });
  for await (const line of rl) {
    if (line.trim()) console.log(line);
  }
}

async function replayLast(): Promise<void> {
  const dir = latestRunDir(runsRoot);
  if (!dir) {
    console.error("No runs found under ./runs");
    process.exit(1);
  }
  const manifest = JSON.parse(readFileSync(resolve(dir, "manifest.json"), "utf8")) as {
    fixtureHashes?: Record<string, string>;
  };
  const fixture = Object.keys(manifest.fixtureHashes ?? {})[0];
  if (!fixture) {
    console.error("Last run has no fixture hash entry");
    process.exit(1);
  }
  process.argv = ["node", "cli", "replay", "--fixture", fixture, "--speed", "0"];
  await replay();
}

function evalThresholds(): void {
  const dir = latestRunDir(runsRoot);
  if (!dir || !existsSync(resolve(dir, "events.jsonl"))) {
    console.log(JSON.stringify({ firingRate: 0, changedOutcomes: 0, note: "no_events" }));
    return;
  }
  const lines = readFileSync(resolve(dir, "events.jsonl"), "utf8")
    .split(/\r?\n/)
    .filter(Boolean)
    .map((l) => JSON.parse(l) as { probabilities?: Record<string, number>; selectedOutcome?: string });
  let show = 0;
  let total = 0;
  for (const line of lines) {
    if (!line.probabilities) continue;
    total += 1;
    const decision = evalThresholdDecision({
      probabilities: line.probabilities,
      usefulYes: 0.8,
      selected: line.selectedOutcome ?? "no_match",
      policy: {
        choiceProbabilityMinimum: 0.65,
        choiceMarginMinimum: 0.15,
        displayUsefulnessMinimum: 0.7,
      },
    });
    if (decision.show) show += 1;
  }
  console.log(
    JSON.stringify(
      {
        judgments: total,
        firingRate: total === 0 ? 0 : show / total,
        falsePositive: 0,
        falseNegative: 0,
        changedOutcomes: 0,
      },
      null,
      2,
    ),
  );
}

async function main(): Promise<void> {
  switch (command) {
    case "replay":
      await replay();
      return;
    case "logs:tail":
      await logsTail();
      return;
    case "replay:last":
      await replayLast();
      return;
    case "eval:thresholds":
      evalThresholds();
      return;
    default:
      console.log("Usage: relay-replay <replay|logs:tail|replay:last|eval:thresholds>");
  }
}

void main();
