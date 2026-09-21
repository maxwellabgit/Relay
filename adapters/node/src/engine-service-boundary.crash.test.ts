import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { createResolveAcronymModule } from "@relay/reflexes";
import { RecordedJudgmentPort, recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

type Harness = Awaited<ReturnType<typeof createNodeHarness>>;

const GENERAL_ASK =
  "What is the difference between connection-oriented and connectionless transport?";
const MODEL_ANSWER = "TCP is connection-oriented; UDP is connectionless.";
const BESS_EXPANSION = "Battery Energy Storage";

/**
 * Phase 3 — crash / restart coverage at each extracted engine service boundary.
 */
describe("engine service boundary crash recovery", () => {
  it("intake: crash after source+case before work claim — processes once with correlation", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    let caseId = "";
    const first = await createNodeHarness({ databasePath, runsRoot });
    try {
      await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      const accepted = await first.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      expect(accepted.ok).toBe(true);
      caseId = accepted.caseId ?? "";
      expect(caseId).toBeTruthy();
      expect(countWork(databasePath, "source.final")).toBe(1);
      expect(workCorrelation(databasePath, "source.final")).toEqual({
        parentWorkId: null,
        correlationId: caseId,
      });
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      await waitFor(async () => {
        const snap = await second.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          (await second.store.getCase(caseId))?.status === "completed" &&
          (await second.store.countWorkItems()) === 0
        );
      });
      const answers = (await second.client.getSnapshot()).feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toContain("Manufacturer Suggested Retail Price");
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("projections/overlays: action cards survive restart without duplication", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const first = await createNodeHarness({ databasePath, runsRoot });
    try {
      await first.client.start();
      await first.client.execute({
        type: "SubmitText",
        text: "Remember that Ada's birthday is March 14",
      });
      await waitFor(async () => {
        const snap = await first.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "confirm_birthday") && (await first.store.countWorkItems()) === 0;
      });
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      const snap = await second.client.getSnapshot();
      expect(snap.actions.filter((a) => a.kind === "confirm_birthday")).toHaveLength(1);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("work dispatcher / outcomes: model boundary resumes once without duplicate answers", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      model: disabledModel(),
    });
    let caseId = "";
    try {
      const accepted = await first.client.execute({ type: "SubmitText", text: GENERAL_ASK });
      caseId = accepted.caseId ?? "";
      expect(caseId).toBeTruthy();
      leaveQueuedModelWork(databasePath, caseId);
      const corr = workCorrelation(databasePath, "model.requested");
      expect(corr.correlationId).toBe(caseId);
      expect(corr.parentWorkId).toBeTruthy();
    } finally {
      await simulateCrash(first);
    }

    let modelCalls = 0;
    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      model: countingModel(() => {
        modelCalls += 1;
        return MODEL_ANSWER;
      }),
    });
    try {
      await second.client.start();
      await waitFor(async () => {
        const snap = await second.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          (await second.store.getCase(caseId))?.status === "completed"
        );
      });
      const answers = (await second.client.getSnapshot()).feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toBe(MODEL_ANSWER);
      expect(modelCalls).toBe(1);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("judgment service: durable judgment work resumes with one answer and labels", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [createResolveAcronymModule()],
    });
    let caseId = "";
    try {
      const accepted = await first.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      caseId = accepted.caseId ?? "";
      leaveQueuedJudgmentWork(databasePath, caseId);
      expect(countWork(databasePath, "judgment.requested")).toBe(1);
      expect(workCorrelation(databasePath, "judgment.requested").correlationId).toBe(caseId);
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [createResolveAcronymModule()],
      judgments: new RecordedJudgmentPort([
        {
          questionSetId: "judgment.acronym-choice",
          response: recordedSuccess({
            expansion: {
              type: "choice",
              choice: BESS_EXPANSION,
              probabilities: { [BESS_EXPANSION]: 0.84, Bessemer: 0.1, no_match: 0.06 },
              confidence: 0.84,
            },
            useful: { type: "noul", probabilityYes: 0.8 },
          }),
        },
      ]),
    });
    try {
      await second.client.start();
      await waitFor(async () => {
        const snap = await second.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          (await second.store.getCase(caseId))?.status === "completed"
        );
      });
      const snap = await second.client.getSnapshot();
      const answers = snap.feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toContain(BESS_EXPANSION);
      const receipts = await second.store.learning.listReceipts();
      const judgmentReceipt = [...receipts].reverse().find((r) => r.caseId === caseId && r.questionType === "choice");
      expect(judgmentReceipt).toBeTruthy();
      expect(Object.keys(judgmentReceipt!.optionLabels).length).toBeGreaterThan(0);
      if (snap.gate) {
        expect(Object.keys(snap.gate.optionLabels).length).toBeGreaterThan(0);
      }
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("learning/pattern: episode recorded at most once across intake restart", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    let caseId = "";
    const first = await createNodeHarness({ databasePath, runsRoot });
    try {
      await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "API",
        expansion: "Application Programming Interface",
        confirmed: true,
      });
      const accepted = await first.client.execute({ type: "SubmitText", text: "What does API mean?" });
      caseId = accepted.caseId ?? "";
      expect(countWork(databasePath, "source.final")).toBe(1);
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      await waitFor(async () => (await second.store.getCase(caseId))?.status === "completed");
      const episodes = await second.store.learning.listEpisodes();
      expect(episodes.filter((e) => e.caseId === caseId).length).toBeLessThanOrEqual(1);
      const answers = (await second.client.getSnapshot()).feedItems.filter((i) => i.kind === "answer");
      expect(answers).toHaveLength(1);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("transactions: sqlite runInTransaction is invoked for state+event boundaries", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const harness = await createNodeHarness({ databasePath, runsRoot });
    try {
      await harness.client.start();
      let sawTxn = false;
      const original = harness.store.runInTransaction!.bind(harness.store);
      harness.store.runInTransaction = async <T>(work: () => Promise<T>) => {
        sawTxn = true;
        return original(work);
      };
      await harness.client.execute({
        type: "UpsertGlossaryEntry",
        token: "TCP",
        expansion: "Transmission Control Protocol",
        confirmed: true,
      });
      await harness.client.execute({ type: "SubmitText", text: "What does TCP mean?" });
      await waitFor(async () => (await harness.store.countWorkItems()) === 0);
      expect(sawTxn).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});

async function tempRoots(): Promise<{ databasePath: string; runsRoot: string }> {
  const root = await mkdtemp(join(tmpdir(), "relay-phase3-"));
  return { databasePath: join(root, "relay.sqlite"), runsRoot: join(root, "runs") };
}

async function simulateCrash(harness: Harness): Promise<void> {
  try {
    harness.close();
  } catch {
    // ignore
  }
}

function countWork(databasePath: string, type: string): number {
  const db = new DatabaseSync(databasePath);
  try {
    const row = db.prepare(`SELECT COUNT(*) AS c FROM work_items WHERE type = ?`).get(type) as { c: number };
    return Number(row.c);
  } finally {
    db.close();
  }
}

function workCorrelation(
  databasePath: string,
  type: string,
): { parentWorkId: string | null; correlationId: string | null } {
  const db = new DatabaseSync(databasePath);
  try {
    const row = db
      .prepare(`SELECT parent_work_id, correlation_id FROM work_items WHERE type = ? LIMIT 1`)
      .get(type) as { parent_work_id: string | null; correlation_id: string | null } | undefined;
    return {
      parentWorkId: row?.parent_work_id ?? null,
      correlationId: row?.correlation_id ?? null,
    };
  } finally {
    db.close();
  }
}

function leaveQueuedModelWork(databasePath: string, caseId: string): void {
  const db = new DatabaseSync(databasePath);
  try {
    const source = db
      .prepare(
        `SELECT work_id, payload_json, priority, created_at FROM work_items WHERE type = 'source.final'`,
      )
      .get() as
      | { work_id: string; payload_json: string; priority: number; created_at: string }
      | undefined;
    expect(source).toBeTruthy();
    const payload = JSON.parse(source!.payload_json) as Record<string, unknown>;
    const at = new Date().toISOString();
    db.prepare(
      `UPDATE cases SET version = 2, status = 'waiting', phase = 'model', wait_kind = 'model', updated_at = ? WHERE case_id = ?`,
    ).run(at, caseId);
    db.prepare(`DELETE FROM work_items WHERE work_id = ?`).run(source!.work_id);
    db.prepare(
      `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at, lease_owner, lease_until, parent_work_id, correlation_id)
       VALUES (?, 'model.requested', ?, ?, ?, ?, NULL, NULL, ?, ?)`,
    ).run(
      `work_${caseId}_model`,
      source!.priority,
      at,
      JSON.stringify({
        caseId,
        caseVersion: 2,
        sourceEventId: payload.sourceEventId,
        textArtifactId: payload.textArtifactId,
        textSha256: payload.textSha256,
      }),
      at,
      source!.work_id,
      caseId,
    );
  } finally {
    db.close();
  }
}

function leaveQueuedJudgmentWork(databasePath: string, caseId: string): void {
  const db = new DatabaseSync(databasePath);
  try {
    const source = db
      .prepare(
        `SELECT work_id, payload_json, priority FROM work_items WHERE type = 'source.final'`,
      )
      .get() as { work_id: string; payload_json: string; priority: number } | undefined;
    expect(source).toBeTruthy();
    const payload = JSON.parse(source!.payload_json) as Record<string, unknown>;
    const at = new Date().toISOString();
    const prompt = JSON.stringify({
      token: "BESS",
      optionIds: [BESS_EXPANSION, "Bessemer"],
      policyVersion: "resolve-acronym@1",
      choiceProbabilityMinimum: 0.65,
      choiceMarginMinimum: 0.15,
      displayUsefulnessMinimum: 0.7,
    });
    db.prepare(
      `UPDATE cases SET version = 2, status = 'waiting', phase = 'judge', wait_kind = 'judgment', updated_at = ? WHERE case_id = ?`,
    ).run(at, caseId);
    db.prepare(`DELETE FROM work_items WHERE work_id = ?`).run(source!.work_id);
    db.prepare(
      `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at, lease_owner, lease_until, parent_work_id, correlation_id)
       VALUES (?, 'judgment.requested', ?, ?, ?, ?, NULL, NULL, ?, ?)`,
    ).run(
      `work_${caseId}_judgment`,
      source!.priority,
      at,
      JSON.stringify({
        caseId,
        token: "BESS",
        reflexId: "reflex.resolve-acronym",
        prompt,
        attempt: 1,
        explicitAsk: true,
        sourceEventId: payload.sourceEventId,
      }),
      at,
      source!.work_id,
      caseId,
    );
  } finally {
    db.close();
  }
}

function disabledModel(): TextModelPort {
  return {
    async generate() {
      return { ok: false, failureReason: "model_disabled" };
    },
  };
}

function countingModel(text: () => string): TextModelPort {
  return {
    async generate() {
      return { ok: true, text: text(), model: "test", elapsedMs: 1 };
    },
  };
}

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 5000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 25));
  }
  throw new Error("waitFor timeout");
}
