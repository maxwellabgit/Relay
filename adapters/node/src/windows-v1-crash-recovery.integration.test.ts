import { mkdtemp, unlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { describe, expect, it } from "vitest";
import type { TextModelPort } from "@relay/contracts";
import { createResolveAcronymModule } from "@relay/reflexes";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

const GENERAL_ASK =
  "What is the difference between connection-oriented and connectionless transport?";
const MODEL_ANSWER = "TCP is connection-oriented; UDP is connectionless.";
const BESS_ASK = "What does BESS mean?";
const BESS_EXPANSION = "Battery Energy Storage";

type Harness = Awaited<ReturnType<typeof createNodeHarness>>;

/**
 * Windows V1 Stage 6 — crash recovery boundaries.
 *
 * These tests intentionally leave mid-flight durable state (queued/leased work,
 * persisted answers/judgments, torn episode rows, missing artifacts) and prove
 * a second runtime recovers without inventing content or duplicating outcomes.
 */
describe("windows v1 crash recovery", () => {
  it("A: crash after source persistence before source work completion — case processed once", async () => {
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
      const accepted = await first.client.execute({
        type: "SubmitText",
        text: "What does MSRP mean?",
      });
      expect(accepted.ok).toBe(true);
      caseId = accepted.caseId ?? "";
      expect(caseId).toBeTruthy();
      // No start(): source + case + source.final are durable; work never claimed.
      expect(countWork(databasePath, "source.final")).toBe(1);
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      await waitFor(async () => {
        const snap = await second.client.getSnapshot();
        const caseRow = await second.store.getCase(caseId);
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          caseRow?.status === "completed" &&
          (await second.store.countWorkItems()) === 0
        );
      });
      const snap = await second.client.getSnapshot();
      const answers = snap.feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toContain("Manufacturer Suggested Retail Price");
      expect(answers[0]?.itemId).toBe(`feed_${caseId}_answer`);
      expect((await second.store.getCase(caseId))?.status).toBe("completed");
      expect(await second.store.countWorkItems()).toBe(0);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("B: crash with model work queued — model resumes; one answer", async () => {
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
      expect(countWork(databasePath, "model.requested")).toBe(1);
      expect(leaseState(databasePath, "model.requested")).toEqual({
        leaseOwner: null,
        leaseUntil: null,
      });
      await second.client.start();
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await second.client.getSnapshot();
      const answers = snap.feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toBe(MODEL_ANSWER);
      expect(answers[0]?.itemId).toBe(`feed_${caseId}_answer`);
      expect(modelCalls).toBe(1);
      expect((await second.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("C: crash while model work is leased — after lease expiration work reclaimed; one answer", async () => {
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
      leaveQueuedModelWork(databasePath, caseId);
      // Process held a lease at the moment of death.
      setLease(databasePath, "model.requested", "engine", "2099-01-01T00:00:00.000Z");
    } finally {
      await simulateCrash(first);
    }

    expect(leaseState(databasePath, "model.requested").leaseOwner).toBe("engine");
    expireLease(databasePath, "model.requested");

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
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await second.client.getSnapshot();
      expect(snap.feedItems.filter((item) => item.kind === "answer")).toHaveLength(1);
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toBe(MODEL_ANSWER);
      expect(modelCalls).toBe(1);
      expect((await second.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("D: crash after answer persisted before case complete — answer reused; no duplicate", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      model: countingModel(() => MODEL_ANSWER),
    });
    let caseId = "";
    try {
      await first.client.start();
      const accepted = await first.client.execute({ type: "SubmitText", text: GENERAL_ASK });
      caseId = accepted.caseId ?? "";
      await waitFor(async () => {
        const snap = await first.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          (await first.store.getCase(caseId))?.status === "completed"
        );
      });
    } finally {
      await first.client.stop();
      first.close();
    }

    leaveAnswerWithoutCaseComplete(databasePath, caseId);

    let modelCalls = 0;
    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      model: {
        async generate() {
          modelCalls += 1;
          throw new Error("model_must_not_run_when_answer_exists");
        },
      },
    });
    try {
      await second.client.start();
      await waitFor(async () => (await second.store.getCase(caseId))?.status === "completed");
      const snap = await second.client.getSnapshot();
      const answers = snap.feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.itemId).toBe(`feed_${caseId}_answer`);
      expect(answers[0]?.summary).toBe(MODEL_ANSWER);
      expect(modelCalls).toBe(0);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("E: crash after Jev request persistence — recovers and retries", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const crashGate = new AbortController();
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [ambiguousBess()],
      judgments: {
        async judge(_request, signal) {
          await hangUntilCrash(crashGate.signal, signal);
          return recordedSuccess({
            expansion: {
              type: "choice",
              choice: BESS_EXPANSION,
              probabilities: { [BESS_EXPANSION]: 0.82, Bessemer: 0.1, no_match: 0.08 },
              confidence: 0.82,
            },
            useful: { type: "noul", probabilityYes: 0.9 },
          });
        },
      },
    });
    let caseId = "";
    try {
      await first.client.start();
      const accepted = await first.client.execute({ type: "SubmitText", text: BESS_ASK });
      caseId = accepted.caseId ?? "";
      await waitFor(async () => judgmentStatus(databasePath) === "requested");
      expect(countWork(databasePath, "judgment.requested")).toBe(1);
      expect(leaseState(databasePath, "judgment.requested").leaseOwner).toBe("engine");
    } finally {
      await simulateCrash(first, crashGate);
    }

    expireLease(databasePath, "judgment.requested");

    let providerCalls = 0;
    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [ambiguousBess()],
      judgments: {
        async judge() {
          providerCalls += 1;
          return recordedSuccess({
            expansion: {
              type: "choice",
              choice: BESS_EXPANSION,
              probabilities: { [BESS_EXPANSION]: 0.82, Bessemer: 0.1, no_match: 0.08 },
              confidence: 0.82,
            },
            useful: { type: "noul", probabilityYes: 0.9 },
          });
        },
      },
    });
    try {
      await second.client.start();
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await second.client.getSnapshot();
      expect(snap.feedItems.filter((item) => item.kind === "answer")).toHaveLength(1);
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toBe(BESS_EXPANSION);
      expect(providerCalls).toBe(1);
      expect(judgmentStatus(databasePath)).toBe("completed");
      expect((await second.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("F: crash after completed Jev response — cached judgment reused; provider not recalled", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    let providerCalls = 0;
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [ambiguousBess()],
      judgments: {
        async judge() {
          providerCalls += 1;
          return recordedSuccess({
            expansion: {
              type: "choice",
              choice: BESS_EXPANSION,
              probabilities: { [BESS_EXPANSION]: 0.82, Bessemer: 0.1, no_match: 0.08 },
              confidence: 0.82,
            },
            useful: { type: "noul", probabilityYes: 0.9 },
          });
        },
      },
    });
    let caseId = "";
    try {
      await first.client.start();
      const accepted = await first.client.execute({ type: "SubmitText", text: BESS_ASK });
      caseId = accepted.caseId ?? "";
      await waitFor(async () => {
        const snap = await first.client.getSnapshot();
        return (
          snap.feedItems.some((item) => item.kind === "answer") &&
          (await first.store.getCase(caseId))?.status === "completed"
        );
      });
      expect(providerCalls).toBe(1);
      expect(judgmentStatus(databasePath)).toBe("completed");
    } finally {
      await first.client.stop();
      first.close();
    }

    requeueCompletedJudgmentWork(databasePath, caseId);

    let secondCalls = 0;
    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      reflexModules: [ambiguousBess()],
      judgments: {
        async judge() {
          secondCalls += 1;
          throw new Error("provider_must_not_be_called_when_completed_judgment_cached");
        },
      },
    });
    try {
      await second.client.start();
      await waitFor(async () => (await second.store.getCase(caseId))?.status === "completed");
      const snap = await second.client.getSnapshot();
      const answers = snap.feedItems.filter((item) => item.kind === "answer");
      expect(answers).toHaveLength(1);
      expect(answers[0]?.summary).toBe(BESS_EXPANSION);
      expect(secondCalls).toBe(0);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("G: crash around completed episode/pattern transaction — one episode; correct pattern count", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const signature = "acronym.lookup|outcome=exact|token=MSRP";
    const episodeId = "episode_case_crash_g";
    const sessionId = "session_crash_g";
    const at = "2026-09-20T18:00:00.000Z";

    const first = await createNodeHarness({ databasePath, runsRoot });
    try {
      // Torn write that atomic recordCompletedEpisode prevents: episode row only.
      const db = new DatabaseSync(databasePath);
      db.prepare(
        `INSERT INTO work_sessions(session_id, started_at, ended_at, termination, episode_count)
         VALUES (?, ?, NULL, 'open', 0)`,
      ).run(sessionId, at);
      db.prepare(
        `INSERT INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
         VALUES (?, ?, ?, ?, 'completed', ?, ?)`,
      ).run(episodeId, sessionId, "case_crash_g", signature, at, at);
      db.close();
    } finally {
      await simulateCrash(first);
    }

    const second = await createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      const pattern = await second.store.learning.recordCompletedEpisode({
        episodeId,
        sessionId,
        caseId: "case_crash_g",
        signature,
        outcome: "completed",
        startedAt: at,
        completedAt: at,
      });
      const again = await second.store.learning.recordCompletedEpisode({
        episodeId,
        sessionId,
        caseId: "case_crash_g",
        signature,
        outcome: "completed",
        startedAt: at,
        completedAt: at,
      });
      const episodes = await second.store.learning.listEpisodes();
      expect(episodes.filter((item) => item.episodeId === episodeId)).toHaveLength(1);
      expect(pattern?.count).toBe(1);
      expect(again?.count).toBe(1);
      expect(again?.evidenceIds).toEqual([episodeId]);
      expect((await second.store.learning.getPattern(signature))?.count).toBe(1);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("H: missing/corrupt protected artifact — unavailable state; engine stays alive", async () => {
    const { databasePath, runsRoot } = await tempRoots();
    const first = await createNodeHarness({
      databasePath,
      runsRoot,
      model: countingModel(() => MODEL_ANSWER),
    });
    let artifactId = "";
    let caseId = "";
    try {
      await first.client.start();
      const accepted = await first.client.execute({ type: "SubmitText", text: GENERAL_ASK });
      caseId = accepted.caseId ?? "";
      await waitFor(async () =>
        (await first.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const records = await first.store.listFeedItemRecords();
      const answer = records.find((item) => item.itemId === `feed_${caseId}_answer`);
      expect(answer).toBeTruthy();
      artifactId = answer!.contentArtifactId;
    } finally {
      await first.client.stop();
      first.close();
    }

    const artifactPath = join(dirname(databasePath), "objects", `${artifactId}.bin`);
    await unlink(artifactPath);

    const second = await createNodeHarness({
      databasePath,
      runsRoot,
      model: countingModel(() => "Engine still answers new asks."),
    });
    try {
      await second.client.start();
      const snap = await second.client.getSnapshot();
      const broken = snap.feedItems.find((item) => item.itemId === `feed_${caseId}_answer`);
      expect(broken?.summary).toBe("[unavailable]");
      expect(snap.status.find((chip) => chip.id === "engine")?.ok).toBe(true);

      await second.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      await second.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      await waitFor(async () => {
        const current = await second.client.getSnapshot();
        return current.feedItems.some(
          (item) => item.kind === "answer" && item.summary.includes("Manufacturer Suggested"),
        );
      });
      const after = await second.client.getSnapshot();
      const healthy = after.feedItems.find(
        (item) => item.kind === "answer" && item.summary.includes("Manufacturer Suggested"),
      );
      expect(healthy).toBeTruthy();
      const healthyRecord = (await second.store.listFeedItemRecords()).find(
        (item) => item.itemId === healthy!.itemId,
      );
      const healthyPath = join(
        dirname(databasePath),
        "objects",
        `${healthyRecord!.contentArtifactId}.bin`,
      );
      await writeFile(healthyPath, Buffer.from("corrupt-bytes-not-matching-sha"));
      const corruptedSnap = await second.client.getSnapshot();
      expect(
        corruptedSnap.feedItems.find((item) => item.itemId === healthy!.itemId)?.summary,
      ).toBe("[unavailable]");
      expect(corruptedSnap.status.find((chip) => chip.id === "engine")?.ok).toBe(true);
      expect(corruptedSnap.feedItems.some((item) => item.summary === MODEL_ANSWER)).toBe(false);
    } finally {
      await second.client.stop();
      second.close();
    }
  });
});

function ambiguousBess() {
  return createResolveAcronymModule({
    glossary: {
      exactUser: async () => null,
      exactProject: async () => null,
      exactBundled: async () => null,
      searchWindow: async (token) => (token === "BESS" ? [BESS_EXPANSION, "Bessemer"] : []),
    },
  });
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
      return { ok: true, text: text(), model: "test-local", elapsedMs: 1 };
    },
  };
}

async function tempRoots(): Promise<{ databasePath: string; runsRoot: string }> {
  const root = await mkdtemp(join(tmpdir(), "relay-crash-"));
  return { databasePath: join(root, "state.sqlite"), runsRoot: join(root, "runs") };
}

async function simulateCrash(harness: Harness, crashGate?: AbortController): Promise<void> {
  // Drop the durable store without draining the scheduler — leased/queued work remains.
  try {
    harness.close();
  } catch {
    /* already closed */
  }
  crashGate?.abort();
  try {
    await Promise.race([harness.client.stop().catch(() => undefined), sleep(150)]);
  } catch {
    /* loop may fail once the DB handle is gone */
  }
}

function hangUntilCrash(...signals: Array<AbortSignal | undefined>): Promise<never> {
  return new Promise((_, reject) => {
    const abort = () => reject(Object.assign(new Error("crashed"), { name: "AbortError" }));
    for (const signal of signals) {
      if (!signal) continue;
      if (signal.aborted) {
        abort();
        return;
      }
      signal.addEventListener("abort", abort, { once: true });
    }
  });
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function openDb(databasePath: string): DatabaseSync {
  return new DatabaseSync(databasePath);
}

function countWork(databasePath: string, type: string): number {
  const db = openDb(databasePath);
  try {
    const row = db.prepare(`SELECT COUNT(*) AS c FROM work_items WHERE type = ?`).get(type) as {
      c: number;
    };
    return row.c;
  } finally {
    db.close();
  }
}

function leaseState(
  databasePath: string,
  type: string,
): { leaseOwner: string | null; leaseUntil: string | null } {
  const db = openDb(databasePath);
  try {
    const row = db
      .prepare(`SELECT lease_owner, lease_until FROM work_items WHERE type = ? LIMIT 1`)
      .get(type) as { lease_owner: string | null; lease_until: string | null } | undefined;
    return { leaseOwner: row?.lease_owner ?? null, leaseUntil: row?.lease_until ?? null };
  } finally {
    db.close();
  }
}

function setLease(databasePath: string, type: string, owner: string, until: string): void {
  const db = openDb(databasePath);
  try {
    db.prepare(`UPDATE work_items SET lease_owner = ?, lease_until = ? WHERE type = ?`).run(
      owner,
      until,
      type,
    );
  } finally {
    db.close();
  }
}

function expireLease(databasePath: string, type: string): void {
  const db = openDb(databasePath);
  try {
    db.prepare(
      `UPDATE work_items SET lease_until = '2000-01-01T00:00:00.000Z' WHERE type = ?`,
    ).run(type);
  } finally {
    db.close();
  }
}

function judgmentStatus(databasePath: string): string | null {
  const db = openDb(databasePath);
  try {
    const row = db
      .prepare(`SELECT status FROM judgments ORDER BY created_at DESC LIMIT 1`)
      .get() as { status: string } | undefined;
    return row?.status ?? null;
  } finally {
    db.close();
  }
}

/** Advance durable state to waiting case + unleased model.requested (never claimed). */
function leaveQueuedModelWork(databasePath: string, caseId: string): void {
  const db = openDb(databasePath);
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
      `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at, lease_owner, lease_until)
       VALUES (?, 'model.requested', ?, ?, ?, ?, NULL, NULL)`,
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
    );
  } finally {
    db.close();
  }
}

/** Case waiting again with answer feed row retained and model work re-queued. */
function leaveAnswerWithoutCaseComplete(databasePath: string, caseId: string): void {
  const db = openDb(databasePath);
  try {
    const source = db
      .prepare(
        `SELECT text_artifact_id, text_sha256, source_event_id FROM source_events ORDER BY created_at DESC LIMIT 1`,
      )
      .get() as
      | { text_artifact_id: string; text_sha256: string; source_event_id: string }
      | undefined;
    expect(source).toBeTruthy();
    const caseRow = db.prepare(`SELECT version FROM cases WHERE case_id = ?`).get(caseId) as {
      version: number;
    };
    const nextVersion = caseRow.version + 1;
    const at = new Date().toISOString();
    db.prepare(
      `UPDATE cases SET version = ?, status = 'waiting', phase = 'model', wait_kind = 'model', updated_at = ? WHERE case_id = ?`,
    ).run(nextVersion, at, caseId);
    db.prepare(`DELETE FROM work_items`).run();
    db.prepare(
      `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at, lease_owner, lease_until)
       VALUES (?, 'model.requested', 100, ?, ?, ?, NULL, NULL)`,
    ).run(
      `work_${caseId}_model`,
      at,
      JSON.stringify({
        caseId,
        caseVersion: nextVersion,
        sourceEventId: source!.source_event_id,
        textArtifactId: source!.text_artifact_id,
        textSha256: source!.text_sha256,
      }),
      at,
    );
    const answer = db
      .prepare(`SELECT item_id FROM feed_items WHERE item_id = ?`)
      .get(`feed_${caseId}_answer`) as { item_id: string } | undefined;
    expect(answer).toBeTruthy();
  } finally {
    db.close();
  }
}

function requeueCompletedJudgmentWork(databasePath: string, caseId: string): void {
  const db = openDb(databasePath);
  try {
    const source = db
      .prepare(`SELECT source_event_id FROM source_events ORDER BY created_at DESC LIMIT 1`)
      .get() as { source_event_id: string } | undefined;
    const caseRow = db.prepare(`SELECT version FROM cases WHERE case_id = ?`).get(caseId) as {
      version: number;
    };
    const nextVersion = caseRow.version + 1;
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
      `UPDATE cases SET version = ?, status = 'waiting', phase = 'judge', wait_kind = 'judgment', updated_at = ? WHERE case_id = ?`,
    ).run(nextVersion, at, caseId);
    db.prepare(`DELETE FROM work_items`).run();
    db.prepare(
      `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at, lease_owner, lease_until)
       VALUES (?, 'judgment.requested', 100, ?, ?, ?, NULL, NULL)`,
    ).run(
      `work_${caseId}_judgment_retry`,
      at,
      JSON.stringify({
        caseId,
        token: "BESS",
        reflexId: "reflex.resolve-acronym",
        prompt,
        attempt: 1,
        explicitAsk: true,
        sourceEventId: source?.source_event_id ?? "",
      }),
      at,
    );
  } finally {
    db.close();
  }
}

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 5000): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error("timeout");
}
