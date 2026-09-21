import { describe, expect, it } from "vitest";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { JudgmentPort } from "@relay/contracts";
import { createNodeHarness } from "./create-client.js";

async function waitFor(
  predicate: () => Promise<boolean>,
  timeoutMs = 2000,
): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 20));
  }
  throw new Error("timeout");
}

describe("autonomous engine", () => {
  it("starts a queue loop independent of React and processes Ask without pausing Listen", async () => {
    const harness = await createNodeHarness({
      ids: (() => {
        let n = 0;
        return { next: (prefix: string) => `${prefix}_${++n}` };
      })(),
    });
    const { client } = harness;
    try {
      await client.start();
      const listen = await client.execute({ type: "SetListening", enabled: true });
      expect(listen.ok).toBe(true);

      const ask = await client.execute({
        type: "SubmitText",
        text: "What does API mean?",
      });
      expect(ask.ok).toBe(true);
      expect(ask.caseId).toBeTruthy();

      await waitFor(async () => {
        const snap = await client.getSnapshot();
        return (
          snap.feedItems.some((i) => i.kind === "answer" || i.kind === "finding") &&
          snap.listening
        );
      });

      const snap = await client.getSnapshot();
      expect(snap.listening).toBe(true);
      expect(snap.feedItems.filter((i) => i.kind === "ask")).toHaveLength(1);
      expect(snap.feedItems.filter((i) => i.kind === "finding")).toHaveLength(0);
      expect(snap.feedItems.filter((i) => i.kind === "answer")).toHaveLength(1);
      expect(snap.feedItems.find((i) => i.kind === "answer")?.summary).toContain(
        "Application Programming Interface",
      );
      expect(snap.status.find((s) => s.id === "model")?.detail).toBe("disabled");
      expect(snap.status.find((s) => s.id === "storage")?.detail).toBe("sqlite");
    } finally {
      await client.stop();
      harness.close();
    }
  });

  it("answers What is MSRP? from the bundled glossary without model or Jev", async () => {
    let modelCalls = 0;
    let jevCalls = 0;
    const harness = await createNodeHarness({
      model: {
        async generate() {
          modelCalls += 1;
          return { ok: false, failureReason: "model_disabled" };
        },
      },
      judgments: {
        async judge() {
          jevCalls += 1;
          return { ok: false, failure: { category: "disabled", message: "not_called" } };
        },
      },
    });
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "What is MSRP?" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer's Suggested Retail Price",
      );
      expect(snap.feedItems.some((item) => item.kind === "task")).toBe(false);
      expect(snap.trace.some((line) => line.type === "policy.evaluated" && line.reasonCode === "exact_glossary")).toBe(
        true,
      );
      expect(snap.trace.some((line) => line.type === "answer.committed")).toBe(true);
      expect(snap.trace.some((line) => line.type.startsWith("model.") || line.type.startsWith("judgment."))).toBe(
        false,
      );
      expect(modelCalls).toBe(0);
      expect(jevCalls).toBe(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("recommends a search task for an unknown acronym instead of inventing a definition", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({
        type: "SubmitText",
        text: "What does ZXQPV mean?",
      });
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return (
          snap.feedItems.some((i) => i.kind === "task") &&
          snap.trace.some((line) => line.type === "outcome.recorded")
        );
      });
      const snap = await harness.client.getSnapshot();
      const tasks = snap.feedItems.filter((i) => i.kind === "task");
      const answers = snap.feedItems.filter((i) => i.kind === "answer" || i.kind === "finding");
      expect(tasks).toHaveLength(1);
      expect(tasks[0]?.summary).toBe("Search online for the definition of ZXQPV");
      expect(answers).toHaveLength(0);
      expect(snap.gate?.reasonCode).toBe("no_candidates");
      expect(snap.gate?.questionType).toBe("not_applicable");
      expect(snap.trace.some((line) => line.type === "outcome.recorded")).toBe(true);
      expect(snap.trace.some((line) => (line.result ?? "").includes("Search online"))).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("persists final source events before scheduling case work", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "BESS glossary check" });
      await waitFor(async () => (await harness.client.getSnapshot()).feedItems.length > 0);
      const segments = await harness.store.listSourceSegments("session_test");
      expect(segments).toHaveLength(1);
      expect(segments[0]?.final).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("stores an explicit glossary memory without asking Jev", async () => {
    const calls: string[] = [];
    const judgments: JudgmentPort = {
      async judge(request) {
        calls.push(request.questionSetId);
        return { ok: false, failure: { category: "missing_secret", message: "typesafe_key_missing" } };
      },
    };
    const harness = await createNodeHarness({ judgments });
    try {
      await harness.client.start();
      const result = await harness.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      expect(result.ok).toBe(true);
      expect(result.summary).toBe("remembered");
      expect(calls).toEqual([]);
      const snap = await harness.client.getSnapshot();
      expect(snap.memories).toEqual([
        {
          kind: "glossary",
          key: "MSRP",
          fields: { expansion: "Manufacturer Suggested Retail Price", status: "confirmed" },
        },
      ]);
      expect(snap.gate?.questionType).toBe("user");
      expect(snap.gate?.reasonCode).toBe("explicit_user");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("retrieves an explicit memory after restart instead of searching again", async () => {
    const databasePath = join(tmpdir(), `relay-memory-${Date.now()}.sqlite`);
    const first = await createNodeHarness({ databasePath });
    try {
      await first.client.start();
      const stored = await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      expect(stored.summary).toBe("remembered");
    } finally {
      await first.client.stop();
      first.close();
    }
    const second = await createNodeHarness({ databasePath });
    try {
      await second.client.start();
      await second.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      await waitFor(async () => (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"));
      const snap = await second.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.kind === "task")).toBe(false);
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer Suggested Retail Price",
      );
      const trace = await second.client.getSnapshot();
      expect(JSON.stringify(trace.trace)).not.toContain("What does MSRP mean?");
    } finally {
      await second.client.stop();
      second.close();
    }
  });
});
