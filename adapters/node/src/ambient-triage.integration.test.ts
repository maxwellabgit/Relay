import { describe, expect, it } from "vitest";
import type { AmbientTriageScores, JudgmentPort } from "@relay/contracts";
import { RecordedJudgmentPort, recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

function micSegment(sessionId: string, segmentId: string, text: string, sequence: number) {
  return {
    schemaVersion: 1 as const,
    sourceId: "mic",
    sessionId,
    segmentId,
    revision: 1,
    sequence,
    startMs: 0,
    endMs: 500,
    speakerKey: null,
    speakerConfidence: null,
    text,
    textConfidence: null,
    final: true as const,
    origin: "microphone" as const,
    cursor: null,
  };
}

function ambientJudgments(scores: Partial<AmbientTriageScores>): JudgmentPort {
  return new RecordedJudgmentPort([
    {
      questionSetId: "judgment.ambient-triage",
      response: recordedSuccess({
        worth_remembering: { type: "noul", probabilityYes: scores.worth_remembering ?? 0.1 },
        possible_fact_claim: { type: "noul", probabilityYes: scores.possible_fact_claim ?? 0.1 },
        possible_correction: { type: "noul", probabilityYes: scores.possible_correction ?? 0.1 },
        possible_commitment: { type: "noul", probabilityYes: scores.possible_commitment ?? 0.1 },
        possible_open_question: { type: "noul", probabilityYes: scores.possible_open_question ?? 0.1 },
        related_to_active_case: { type: "noul", probabilityYes: scores.related_to_active_case ?? 0.1 },
        interrupt_worthy: { type: "noul", probabilityYes: scores.interrupt_worthy ?? 0.1 },
      }),
    },
  ]);
}

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 8000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error("timeout");
}

async function startListeningHarness(
  sessionId: string,
  judgments: JudgmentPort,
  model?: { generate(): Promise<{ ok: true; text: string; model: string; elapsedMs: number }> },
) {
  const harness = await createNodeHarness({
    sessionId,
    judgments,
    ...(model ? { model } : {}),
  });
  await harness.client.start();
  await harness.store.setHostedProcessingEnabled(true);
  await harness.client.execute({ type: "SetHostedProcessing", enabled: true });
  await harness.client.execute({ type: "SetListening", enabled: true });
  return harness;
}

describe("ambient candidate triage", () => {
  it("ignores ordinary chatter", async () => {
    const harness = await startListeningHarness("ambient_ignore", ambientJudgments({}));
    try {
      await harness.engine.ingestFinalSegment(
        micSegment("ambient_ignore", "seg_ignore", "Yeah the weather is nice today", 1),
        false,
      );
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.cases.every((c) => c.status === "completed");
      });
      const snap = await harness.client.getSnapshot();
      expect(snap.actions.some((a) => a.kind === "ambient_recommendation")).toBe(false);
      const events = await harness.store.listCandidateEvents();
      expect(events.some((e) => e.status === "ignored")).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("surfaces a quiet note recommendation for durable information", async () => {
    const harness = await startListeningHarness(
      "ambient_note",
      ambientJudgments({ worth_remembering: 0.85 }),
    );
    try {
      await harness.engine.ingestFinalSegment(
        micSegment(
          "ambient_note",
          "seg_note",
          "Remember, our deployment target is Azure West US",
          1,
        ),
        false,
      );
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "ambient_recommendation");
      });
      const snap = await harness.client.getSnapshot();
      const card = snap.actions.find((a) => a.kind === "ambient_recommendation");
      expect(card?.primary).toBe("save");
      expect(card?.title).toMatch(/Save this/i);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("routes corrections toward a save recommendation", async () => {
    const harness = await startListeningHarness(
      "ambient_correction",
      ambientJudgments({ possible_correction: 0.82, worth_remembering: 0.65 }),
    );
    try {
      await harness.engine.ingestFinalSegment(
        micSegment(
          "ambient_correction",
          "seg_correction",
          "Actually the API endpoint is v2, not v1",
          1,
        ),
        false,
      );
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "ambient_recommendation");
      });
      const snap = await harness.client.getSnapshot();
      const card = snap.actions.find((a) => a.kind === "ambient_recommendation");
      expect(card?.reason).toMatch(/correction/i);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("suggests a task for commitments", async () => {
    const harness = await startListeningHarness(
      "ambient_commitment",
      ambientJudgments({ possible_commitment: 0.82 }),
    );
    try {
      await harness.engine.ingestFinalSegment(
        micSegment("ambient_commitment", "seg_commit", "I'll finish the report by Friday", 1),
        false,
      );
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "ambient_recommendation");
      });
      const snap = await harness.client.getSnapshot();
      const card = snap.actions.find((a) => a.kind === "ambient_recommendation");
      expect(card?.primary).toBe("create_task");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("interrupts only with urgency and a high interrupt score", async () => {
    const harness = await startListeningHarness(
      "ambient_urgency",
      ambientJudgments({ interrupt_worthy: 0.95, possible_commitment: 0.5 }),
    );
    try {
      await harness.engine.ingestFinalSegment(
        micSegment(
          "ambient_urgency",
          "seg_urgency",
          "We need to ship this ASAP, it's blocking the release",
          1,
        ),
        false,
      );
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "ambient_recommendation" && a.quiet === false);
      });
      const snap = await harness.client.getSnapshot();
      const card = snap.actions.find((a) => a.kind === "ambient_recommendation");
      expect(card?.quiet).toBe(false);
      expect(card?.primary).toBe("review");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("resumes ambient triage after hosted processing is enabled", async () => {
    const sessionId = "ambient_hosted_resume";
    const harness = await createNodeHarness({
      sessionId,
      judgments: ambientJudgments({ worth_remembering: 0.85 }),
    });
    try {
      await harness.client.start();
      await harness.store.setHostedProcessingEnabled(false);
      await harness.client.execute({ type: "SetHostedProcessing", enabled: false });
      await harness.client.execute({ type: "SetListening", enabled: true });

      await harness.engine.ingestFinalSegment(
        micSegment(sessionId, "seg_hosted_wait", "Remember, staging deploys on Tuesdays", 1),
        false,
      );

      await waitFor(async () => {
        const active = await harness.store.listActiveCases();
        return active.some((c) => c.status === "waiting" && c.waitKind === "hosted_judgment");
      });

      const waiting = await harness.client.getSnapshot();
      expect(waiting.actions.some((a) => a.kind === "ambient_recommendation")).toBe(false);

      const enableResult = await harness.client.execute({ type: "SetHostedProcessing", enabled: true });
      expect(enableResult.summary).toMatch(/hosted_processing_on_resumed_1/);

      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.actions.some((a) => a.kind === "ambient_recommendation");
      });

      const after = await harness.client.getSnapshot();
      expect(after.cases.every((c) => c.status === "completed")).toBe(true);
      expect(after.actions.some((a) => a.kind === "ambient_recommendation")).toBe(true);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("keeps Ask working in parallel while listening", async () => {
    const harness = await startListeningHarness(
      "ambient_parallel_ask",
      ambientJudgments({ worth_remembering: 0.85 }),
      {
        async generate() {
          return { ok: true, text: "DNS maps names to addresses.", model: "test", elapsedMs: 1 };
        },
      },
    );
    try {
      const observed = harness.engine.ingestFinalSegment(
        micSegment(
          "ambient_parallel_ask",
          "seg_observed",
          "Remember, the canonical API URL is https://api.example.com",
          1,
        ),
        false,
      );
      await harness.client.execute({ type: "SubmitText", text: "What is DNS?" });
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        return snap.feedItems.some((item) => item.kind === "ask");
      });
      expect(await observed).toMatch(/^case_/);
      const snap = await harness.client.getSnapshot();
      expect(snap.listening).toBe(true);
      expect(snap.feedItems.some((item) => item.kind === "ask")).toBe(true);
      expect(snap.cases.filter((c) => c.origin === "observed").length).toBeGreaterThanOrEqual(1);
      expect(snap.cases.filter((c) => c.origin === "direct").length).toBeGreaterThanOrEqual(1);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});
