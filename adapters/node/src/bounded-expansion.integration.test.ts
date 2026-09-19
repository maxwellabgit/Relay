import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { JudgmentPort } from "@relay/contracts";
import { createResolveAcronymModule } from "@relay/reflexes";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

const SENTINEL = "PRIVACY_SENTINEL_7f3a9c2e";

describe("bounded expansion through RelayClient", () => {
  it("does not treat three unrelated unknowns as one repeated task", async () => {
    const harness = createNodeHarness();
    try {
      await harness.client.start();
      for (const token of ["MSRP", "BESS", "OEM"]) {
        await harness.client.execute({ type: "SubmitText", text: `What does ${token} mean?` });
        await waitFor(async () =>
          (await harness.client.getSnapshot()).feedItems.some((item) => item.summary.includes(token) && item.kind === "task"),
        );
        await harness.client.execute({ type: "EndWorkSession" });
      }
      const snap = await harness.client.getSnapshot();
      expect(snap.patterns).toHaveLength(3);
      expect(snap.patterns.every((pattern) => pattern.candidateState !== "proposed")).toBe(true);
      expect(snap.review.approvedReflexes).toBe(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("recommends a calendar block only after equivalent episodes in distinct sessions", async () => {
    const judgments: JudgmentPort = {
      async judge(request) {
        if (request.questionSetId !== "judgment.expansion-benefit") {
          return { ok: false, failure: { category: "disabled", message: "recorded_judgment_missing" } };
        }
        return recordedSuccess({ benefit: { type: "noul", probabilityYes: 0.8 } });
      },
    };
    const harness = createNodeHarness({ judgments });
    try {
      await harness.client.start();
      const fields = { start_bucket: "noon", duration: "60m", reminder_offset: "-1d" } as const;
      for (let index = 0; index < 3; index += 1) {
        await harness.client.execute({ type: "RecordCompletedWork", kind: "calendar.block", fields });
        await harness.client.execute({ type: "EndWorkSession" });
      }
      const snap = await harness.client.getSnapshot();
      const pattern = snap.patterns[0];
      expect(pattern?.because).toBe(
        "Observed 3 completed calendar-block episodes across 3 sessions: 60 minutes near noon, reminder one day before. Recommend creating a Calendar Block Reflex?",
      );
      expect(pattern?.candidateState).toBe("proposed");
      const receipts = await harness.store.learning.listReceipts();
      expect(snap.gate?.probabilities).toEqual(receipts.at(-1)?.probabilities);
      const approved = await harness.client.execute({
        type: "ApproveCandidate",
        candidateId: `cand_${pattern?.signature}`,
      });
      expect(approved.summary).toBe("approved");
      expect((await harness.client.getSnapshot()).review.approvedReflexes).toBe(1);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("shows a bounded Jev failure for an ambiguous acronym and does not invent a definition", async () => {
    const harness = createNodeHarness({
      reflexModules: [ambiguous()],
      judgments: {
        async judge() {
          return { ok: false, failure: { category: "missing_secret", message: "typesafe_key_missing" } };
        },
      },
    });
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "wait"));
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.kind === "task")).toBe(false);
      expect(snap.feedItems.find((item) => item.kind === "wait")?.summary).toContain("missing_secret");
      expect(snap.gate?.result).toBe("wait");
      expect(await harness.store.listDeadLetters()).toHaveLength(1);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("completes an ambiguous choice when Jev returns a probability above the threshold", async () => {
    const harness = createNodeHarness({
      reflexModules: [ambiguous()],
      judgments: {
        async judge() {
          return recordedSuccess({
            expansion: {
              type: "choice",
              choice: "Battery Energy Storage",
              probabilities: { "Battery Energy Storage": 0.82, Bessemer: 0.1, no_match: 0.08 },
              confidence: 0.82,
            },
          });
        },
      },
    });
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"));
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toBe("Battery Energy Storage");
      expect(snap.gate?.result).toBe("pass");
      expect(snap.gate?.probabilities["Battery Energy Storage"]).toBe(0.82);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("stores a confirmed birthday and keeps the sentinel out of the run log", async () => {
    const runsRoot = await mkdtemp(join(tmpdir(), "relay-privacy-"));
    const databasePath = join(runsRoot, "state.sqlite");
    const first = createNodeHarness({ databasePath, runsRoot });
    try {
      await first.client.start();
      expect(
        (await first.client.execute({ type: "CaptureBirthday", personKey: "Ada", date: "1990-02-31", confirmed: true }))
          .summary,
      ).toBe("invalid_date");
      expect(
        (await first.client.execute({ type: "CaptureBirthday", personKey: "Ada", date: "1990-02-02", confirmed: false }))
          .summary,
      ).toBe("confirmation_required");
      expect(
        (await first.client.execute({ type: "CaptureBirthday", personKey: "Ada", date: "1990-02-02", confirmed: true }))
          .summary,
      ).toBe("birthday_stored");
      await first.client.execute({ type: "SubmitText", text: `note ${SENTINEL}` });
      await waitFor(async () => (await first.client.getSnapshot()).feedItems.length > 0);
      const logPath = (await first.client.getSnapshot()).runtime.logPath;
      const log = await readFile(logPath, "utf8");
      expect(log).not.toContain(SENTINEL);
      expect(log).not.toContain("1990-02-02");
    } finally {
      await first.client.stop();
      first.close();
    }
    const second = createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      const snap = await second.client.getSnapshot();
      expect(snap.memories).toContainEqual({ kind: "birthday", key: "Ada", fields: { date: "1990-02-02" } });
    } finally {
      await second.client.stop();
      second.close();
    }
  });
});

function ambiguous() {
  return createResolveAcronymModule({
    glossary: {
      exactProject: () => null,
      exactGlobal: (token) => (token === "API" ? "Application Programming Interface" : null),
      searchWindow: (token) => (token === "BESS" ? ["Battery Energy Storage", "Bessemer"] : []),
    },
  });
}

async function waitFor(predicate: () => Promise<boolean>): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < 2000) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error("timeout");
}
