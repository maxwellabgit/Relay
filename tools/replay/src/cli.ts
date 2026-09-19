#!/usr/bin/env node
import { resolve } from "node:path";
import { createNodeHarness } from "@relay/adapter-node";
import { runReplay } from "@relay/testkit";

const args = process.argv.slice(2);
const command = args[0] ?? "help";

function flag(name: string): string | undefined {
  const idx = args.indexOf(name);
  if (idx < 0) return undefined;
  return args[idx + 1];
}

async function replay(): Promise<void> {
  const fixture = flag("--fixture") ?? args.find((a) => a.endsWith(".jsonl"));
  const speedRaw = flag("--speed") ?? args.find((a, i) => args[i - 1] === "--speed") ?? "0";
  const speed = Number(speedRaw);
  if (!fixture || Number.isNaN(speed)) {
    console.error("Usage: npm run replay -- --fixture <path> --speed <0|1|10>");
    process.exit(1);
  }
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
    console.log(
      JSON.stringify(
        {
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
  }
}

async function main(): Promise<void> {
  switch (command) {
    case "replay":
      await replay();
      return;
    case "logs:tail":
      console.log("logs:tail lands in commit 6");
      return;
    case "replay:last":
      console.log("replay:last lands in commit 6");
      return;
    case "eval:thresholds":
      console.log("eval:thresholds lands in commit 6");
      return;
    default:
      console.log("Usage: relay-replay <replay|logs:tail|replay:last|eval:thresholds>");
  }
}

void main();
