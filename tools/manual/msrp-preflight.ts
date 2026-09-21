import { mkdtemp, readdir, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { JudgmentPort, TextModelPort } from "@relay/contracts";
import { createNodeHarness } from "@relay/adapter-node";

const ASK = "What is MSRP?";
const EXPANSION = "Manufacturer's Suggested Retail Price";
const EXPECTED = [
  ["source.accepted", "direct_answer"],
  ["case.created", "direct_answer"],
  ["reflex.detected", null],
  ["policy.evaluated", "exact_glossary"],
  ["answer.committed", null],
  ["episode.recorded", "completed"],
  ["outcome.recorded", null],
  ["run.ended", null],
] as const;

let modelCalls = 0;
let jevCalls = 0;

const model: TextModelPort = {
  async generate() {
    modelCalls += 1;
    return { ok: false, failureReason: "model_disabled" };
  },
};

const judgments: JudgmentPort = {
  async judge() {
    jevCalls += 1;
    return { ok: false, failure: { category: "disabled", message: "not_called" } };
  },
};

const root = await mkdtemp(join(tmpdir(), "relay-msrp-manual-"));
const harness = await createNodeHarness({
  databasePath: join(root, "state.sqlite"),
  runsRoot: join(root, "runs"),
  model,
  judgments,
});

try {
  await harness.client.start();
  const accepted = await harness.client.execute({ type: "SubmitText", text: ASK });
  if (!accepted.ok) {
    throw new Error(`ask rejected: ${accepted.summary}`);
  }
  const deadline = Date.now() + 5000;
  let summary = "";
  while (Date.now() < deadline) {
    const snap = await harness.client.getSnapshot();
    summary = snap.feedItems.find((item) => item.kind === "answer")?.summary ?? "";
    if (summary.includes(EXPANSION)) break;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  if (!summary.includes(EXPANSION)) {
    throw new Error(`missing answer: ${summary || "(none)"}`);
  }
  if (modelCalls !== 0 || jevCalls !== 0) {
    throw new Error(`modelCalls=${modelCalls} jevCalls=${jevCalls}`);
  }
} finally {
  await harness.client.stop();
  harness.close();
}

const runs = await readdir(join(root, "runs"));
const runDir = runs.find((name) => name.startsWith("run_"));
if (!runDir) throw new Error("missing run directory");
const lines = (await readFile(join(root, "runs", runDir, "events.jsonl"), "utf8"))
  .split("\n")
  .map((line) => line.trim())
  .filter(Boolean)
  .map((line) => JSON.parse(line) as { eventType?: string; reasonCode?: string });

let cursor = 0;
for (const [eventType, reasonCode] of EXPECTED) {
  const found = lines.slice(cursor).findIndex((line) => {
    if (line.eventType !== eventType) return false;
    return reasonCode == null || line.reasonCode === reasonCode;
  });
  if (found < 0) {
    throw new Error(`missing ${eventType}${reasonCode ? ` ${reasonCode}` : ""}`);
  }
  const line = lines[cursor + found];
  console.log(`${(line?.eventType ?? eventType).padEnd(22)}${line?.reasonCode ?? ""}`);
  cursor += found + 1;
}

const forbidden = lines.filter(
  (line) => line.eventType?.startsWith("model.") || line.eventType?.startsWith("judgment."),
);
if (forbidden.length > 0) {
  throw new Error(
    `unexpected provider events: ${forbidden.map((line) => line.eventType).join(",")}`,
  );
}

console.log("MSRP manual-test preflight PASS");
