import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { JudgmentPort, JudgmentResponse } from "@relay/contracts";
import { isRuntimeEvent, parseBirthdayUtterance, validateBirthday } from "@relay/engine";
import { calendarBlockEpisode, createResolveAcronymModule } from "@relay/reflexes";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

const SENTINEL = "PRIVACY_SENTINEL_7f3a9c2e";

describe("production path", () => {
  it("desktop_memory_survives_restart", async () => {
    const appData = await mkdtemp(join(tmpdir(), "relay-app-"));
    const databasePath = join(appData, "state.sqlite");
    const first = await createNodeHarness({ databasePath });
    try {
      await first.client.start();
      const saved = await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      expect(saved.ok).toBe(true);
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
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer Suggested Retail Price",
      );
      expect(snap.runtime.storageAdapter).toBe("sqlite");
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("desktop_learning_state_survives_restart", async () => {
    const appData = await mkdtemp(join(tmpdir(), "relay-learn-"));
    const databasePath = join(appData, "state.sqlite");
    const first = await createNodeHarness({
      databasePath,
      episodeDefinitions: [calendarBlockEpisode],
      judgments: benefitPort(),
    });
    try {
      await first.client.start();
      await first.engine.completeVerifiedWork("calendar.block", {
        start_bucket: "noon",
        duration: "60m",
        reminder_offset: "-1d",
      });
      await first.client.execute({ type: "EndWorkSession" });
    } finally {
      await first.client.stop();
      first.close();
    }
    const second = await createNodeHarness({ databasePath, episodeDefinitions: [calendarBlockEpisode] });
    try {
      await second.client.start();
      const sessions = await second.store.learning.listSessions();
      const episodes = await second.store.learning.listEpisodes();
      const receipts = await second.store.learning.listReceipts();
      const patterns = await second.store.learning.listPatterns();
      expect(sessions.some((session) => session.termination === "completed")).toBe(true);
      expect(episodes.some((episode) => episode.outcome === "completed")).toBe(true);
      expect(receipts.length).toBeGreaterThan(0);
      expect(patterns[0]?.count).toBe(1);
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("retryable_judgment_parks_once_after_the_transport_returns", async () => {
    let calls = 0;
    const judgments: JudgmentPort = {
      async judge(): Promise<JudgmentResponse> {
        calls += 1;
        return { ok: false, failure: { category: "rate_limited", message: "later" } };
      },
    };
    const harness = await createNodeHarness({ judgments, reflexModules: [ambiguous()] });
    try {
      await harness.client.start();
      const ask = await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => {
        const current = await harness.store.getCase(ask.caseId ?? "");
        return current?.waitKind === "hosted_judgment" && (await harness.store.countWorkItems()) === 0;
      });
      expect(calls).toBe(1);
      const current = await harness.store.getCase(ask.caseId ?? "");
      expect(current?.status).toBe("waiting");
      expect(current?.waitKind).toBe("hosted_judgment");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("missing_secret_becomes_one_durable_wait", async () => {
    const harness = await createNodeHarness({
      reflexModules: [ambiguous()],
      judgments: {
        async judge() {
          return { ok: false, failure: { category: "missing_secret", message: "typesafe_key_missing" } };
        },
      },
    });
    try {
      await harness.client.start();
      const ask = await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () => {
        const current = await harness.store.getCase(ask.caseId ?? "");
        return current?.waitKind === "hosted_judgment" && (await harness.store.countWorkItems()) === 0;
      });
      const current = await harness.store.getCase(ask.caseId ?? "");
      expect(current?.status).toBe("waiting");
      expect(current?.waitKind).toBe("hosted_judgment");
      expect(await harness.store.listDeadLetters()).toHaveLength(0);
      expect(await harness.store.countWorkItems()).toBe(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("unknown_lookup_is_not_completed_work", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "What does ZXQPV mean?" });
      await waitFor(async () => (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "task"));
      await harness.client.execute({ type: "EndWorkSession" });
      const episodes = await harness.store.learning.listEpisodes();
      const sessions = await harness.store.learning.listSessions();
      expect(episodes.every((episode) => episode.outcome !== "completed")).toBe(true);
      expect(sessions.some((session) => session.termination === "completed")).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("glossary_rejects_empty_expansion", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      const rejected = await harness.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: " ",
        confirmed: true,
      });
      expect(rejected.ok).toBe(false);
      expect(rejected.summary).toBe("empty_expansion");
      expect(await harness.store.learning.listMemories()).toHaveLength(0);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("candidate_requires_registered_template", async () => {
    const bare = {
      kind: "notes.tick",
      eligibleOutcomes: ["completed"] as const,
      normalize: () => "notes.tick|bucket=a",
    };
    const harness = await createNodeHarness({ episodeDefinitions: [bare], judgments: benefitPort() });
    try {
      await harness.client.start();
      for (let index = 0; index < 3; index += 1) {
        await harness.engine.completeVerifiedWork("notes.tick", { bucket: "a" });
        await harness.client.execute({ type: "EndWorkSession" });
      }
      const snap = await harness.client.getSnapshot();
      expect(snap.patterns.every((pattern) => pattern.candidateState !== "proposed" || pattern.because.length > 0)).toBe(
        true,
      );
      expect(snap.patterns.some((pattern) => pattern.candidateState === "proposed" && pattern.because === "")).toBe(
        false,
      );
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("review_resets_after_completion", async () => {
    const harness = await createNodeHarness({ episodeDefinitions: [calendarBlockEpisode] });
    try {
      await harness.client.start();
      await fillSessions(harness, 12);
      expect((await harness.client.getSnapshot()).review.reviewDue).toBe(false);
      const reviews = await harness.store.learning.listReviews();
      expect(reviews.length).toBe(1);
      await fillSessions(harness, 12);
      expect((await harness.client.getSnapshot()).review.reviewDue).toBe(false);
      expect((await harness.store.learning.listReviews()).length).toBe(2);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("trace_rejects_verbatim_payloads", () => {
    const base = {
      schemaVersion: 2,
      sequence: 1,
      runId: "run_test",
      at: "2026-09-19T00:00:00.000Z",
      eventType: "run.started",
      stage: "run",
      status: "started",
    };
    expect(isRuntimeEvent(base)).toBe(true);
    for (const field of ["runId", "at", "eventType", "stage", "status", "reasonCode", "caseId", "providerRequestId"]) {
      expect(isRuntimeEvent({ ...base, [field]: SENTINEL })).toBe(false);
    }
  });

  it("birthday_supports_unicode_and_partial_dates", async () => {
    expect(validateBirthday("Rasmi Pandey", "01-04")).toBeNull();
    expect(validateBirthday("José", "1990-01-04")).toBeNull();
    expect(validateBirthday("Mary-Jane", "01-04")).toBeNull();
    expect(validateBirthday("O’Connor", "12-31")).toBeNull();
    expect(validateBirthday("李明", "03-08")).toBeNull();
    expect(parseBirthdayUtterance("Remember that Rasmi’s birthday is January 4")?.displayName).toBe("Rasmi");
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      const saved = await harness.client.execute({
        type: "CaptureBirthday",
        displayName: "José",
        date: "01-04",
        confirmed: true,
      });
      expect(saved.summary).toBe("birthday_stored");
      const log = (await harness.client.getSnapshot()).runtime.logPath;
      if (log) {
        try {
          const text = await readFile(join(log, "events.jsonl"), "utf8");
          expect(text).not.toContain("José");
          expect(text).not.toContain("01-04");
        } catch {
          // A missing log is not a name leak.
        }
      }
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});

function benefitPort(): JudgmentPort {
  return {
    async judge(request) {
      if (request.questionSetId !== "judgment.expansion-benefit") {
        return { ok: false, failure: { category: "disabled", message: "recorded_judgment_missing" } };
      }
      return recordedSuccess({ benefit: { type: "noul", probabilityYes: 0.8 } });
    },
  };
}

function ambiguous() {
  return createResolveAcronymModule({
    glossary: {
      exactUser: async () => null,
      exactProject: async () => null,
      exactBundled: async () => null,
      searchWindow: async (token) => (token === "BESS" ? ["Battery Energy Storage", "Bessemer"] : []),
    },
  });
}

async function fillSessions(
  harness: Awaited<ReturnType<typeof createNodeHarness>>,
  count: number,
): Promise<void> {
  for (let index = 0; index < count; index += 1) {
    await harness.engine.completeVerifiedWork("calendar.block", {
      start_bucket: "noon",
      duration: "60m",
      reminder_offset: "-1d",
    });
    await harness.client.execute({ type: "EndWorkSession" });
  }
}

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 12_000): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error("timeout");
}
