import { describe, expect, it, vi } from "vitest";
import type { ArtifactStorePort, DataPolicy, JudgmentRecord } from "@relay/contracts";
import type { EngineStore } from "./store.js";
import { runJudgmentLifecycle } from "./judgment-lifecycle.js";

function memoryArtifacts(): ArtifactStorePort {
  const blobs = new Map<string, Uint8Array>();
  let n = 0;
  return {
    async put(bytes: Uint8Array, _policy: DataPolicy) {
      void _policy;
      const artifactId = `art_${++n}`;
      const digest = await crypto.subtle.digest("SHA-256", bytes);
      const sha256 = [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
      blobs.set(`${artifactId}:${sha256}`, bytes);
      return { artifactId, sha256, policy: { disclosure: "local_only" as const, sensitivity: 0 } };
    },
    async get(ref) {
      const bytes = blobs.get(`${ref.artifactId}:${ref.sha256}`);
      if (!bytes) throw new Error("missing_artifact");
      return bytes;
    },
  };
}

function judgmentStore(): Pick<
  EngineStore,
  "findCompletedJudgmentByHash" | "upsertJudgment" | "getHostedProcessingEnabled" | "setHostedProcessingEnabled"
> & { hosted: boolean; judgments: JudgmentRecord[] } {
  const state = { hosted: false, judgments: [] as JudgmentRecord[] };
  return {
    get hosted() {
      return state.hosted;
    },
    set hosted(value: boolean) {
      state.hosted = value;
    },
    get judgments() {
      return state.judgments;
    },
    async findCompletedJudgmentByHash() {
      return null;
    },
    async upsertJudgment(record) {
      state.judgments.push(record);
    },
    async getHostedProcessingEnabled() {
      return state.hosted;
    },
    async setHostedProcessingEnabled(enabled: boolean) {
      state.hosted = enabled;
    },
  };
}

describe("runJudgmentLifecycle hosted processing gate", () => {
  it("does not dispatch when hosted processing is disabled", async () => {
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: {
        model: "jev",
        answers: {},
        inputTokens: 1,
        outputTokens: 1,
        elapsedMs: 1,
      },
    }));
    const store = judgmentStore();
    store.hosted = false;
    const artifacts = memoryArtifacts();
    const outcome = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts,
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_1` },
        isHostedProcessingAllowed: () => store.getHostedProcessingEnabled(),
      },
      {
        questionSetId: "judgment.acronym-choice",
        questionSetVersion: "1",
        model: "jev-latest",
        state: { token: "API" },
        questions: {
          expansion: {
            type: "choice",
            instructions: "pick",
            criteria: { a: "A", no_match: "none" },
            requireNoMatch: true,
          },
        },
      },
      new AbortController().signal,
    );
    expect(judge).not.toHaveBeenCalled();
    expect(outcome.providerCalled).toBe(false);
    expect(outcome.response.ok).toBe(false);
    if (!outcome.response.ok) {
      expect(outcome.response.failure.message).toBe("hosted_processing_disabled");
      expect(outcome.response.failure.category).toBe("disabled");
    }
    expect(outcome.record.failureCategory).toBe("hosted_processing_disabled");
  });

  it("fails closed when isHostedProcessingAllowed is omitted", async () => {
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: { model: "jev", answers: {}, inputTokens: 1, outputTokens: 1, elapsedMs: 1 },
    }));
    const store = judgmentStore();
    store.hosted = true;
    const outcome = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts: memoryArtifacts(),
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_omit` },
      },
      {
        questionSetId: "judgment.acronym-choice",
        questionSetVersion: "1",
        model: "jev-latest",
        state: { token: "API" },
        questions: {
          expansion: {
            type: "choice",
            instructions: "pick",
            criteria: { a: "A", no_match: "none" },
            requireNoMatch: true,
          },
        },
      },
      new AbortController().signal,
    );
    expect(judge).not.toHaveBeenCalled();
    expect(outcome.providerCalled).toBe(false);
    if (!outcome.response.ok) {
      expect(outcome.response.failure.category).toBe("not_authorized");
    }
  });

  it("fails closed with not_authorized when settings lookup throws", async () => {
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: { model: "jev", answers: {}, inputTokens: 1, outputTokens: 1, elapsedMs: 1 },
    }));
    const store = judgmentStore();
    const outcome = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts: memoryArtifacts(),
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_throw` },
        isHostedProcessingAllowed: async () => {
          throw new Error("settings_unavailable");
        },
      },
      {
        questionSetId: "judgment.acronym-choice",
        questionSetVersion: "1",
        model: "jev-latest",
        state: { token: "API" },
        questions: {
          expansion: {
            type: "choice",
            instructions: "pick",
            criteria: { a: "A", no_match: "none" },
            requireNoMatch: true,
          },
        },
      },
      new AbortController().signal,
    );
    expect(judge).not.toHaveBeenCalled();
    expect(outcome.providerCalled).toBe(false);
    if (!outcome.response.ok) {
      expect(outcome.response.failure.category).toBe("not_authorized");
    }
    expect(outcome.record.failureCategory).toBe("not_authorized");
  });

  it("reuses cached completed judgment when hosted processing is later OFF", async () => {
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: {
        model: "jev",
        answers: {
          expansion: {
            type: "choice" as const,
            choice: "a",
            probabilities: { a: 0.9, no_match: 0.1 },
            confidence: 0.9,
          },
        },
        inputTokens: 1,
        outputTokens: 1,
        elapsedMs: 1,
      },
    }));
    const store = judgmentStore();
    store.hosted = true;
    const artifacts = memoryArtifacts();
    const request = {
      questionSetId: "judgment.acronym-choice",
      questionSetVersion: "1",
      model: "jev-latest",
      state: { token: "API" },
      questions: {
        expansion: {
          type: "choice" as const,
          instructions: "pick",
          criteria: { a: "A", no_match: "none" },
          requireNoMatch: true,
        },
      },
    };
    const first = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts,
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_cache1` },
        isHostedProcessingAllowed: () => store.getHostedProcessingEnabled(),
      },
      request,
      new AbortController().signal,
    );
    expect(first.providerCalled).toBe(true);
    expect(first.response.ok).toBe(true);
    const completed = store.judgments.find((j) => j.status === "completed");
    expect(completed?.requestHash).toBeTruthy();
    store.findCompletedJudgmentByHash = async (hash: string) =>
      store.judgments.find((j) => j.requestHash === hash && j.status === "completed") ?? null;
    store.hosted = false;
    const second = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts,
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:01.000Z") },
        ids: { next: (prefix) => `${prefix}_cache2` },
        isHostedProcessingAllowed: () => store.getHostedProcessingEnabled(),
      },
      request,
      new AbortController().signal,
    );
    expect(judge).toHaveBeenCalledOnce();
    expect(second.providerCalled).toBe(false);
    expect(second.response.ok).toBe(true);
  });

  it("dispatches when hosted processing is enabled", async () => {
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: {
        model: "jev",
        answers: {
          expansion: {
            type: "choice" as const,
            choice: "a",
            probabilities: { a: 0.9, no_match: 0.1 },
            confidence: 0.9,
          },
        },
        inputTokens: 1,
        outputTokens: 1,
        elapsedMs: 1,
      },
    }));
    const store = judgmentStore();
    store.hosted = true;
    const artifacts = memoryArtifacts();
    const outcome = await runJudgmentLifecycle(
      {
        store: store as unknown as EngineStore,
        artifacts,
        judgments: { judge },
        clock: { now: () => new Date("2026-01-01T00:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_1` },
        isHostedProcessingAllowed: () => store.getHostedProcessingEnabled(),
      },
      {
        questionSetId: "judgment.acronym-choice",
        questionSetVersion: "1",
        model: "jev-latest",
        state: { token: "API" },
        questions: {
          expansion: {
            type: "choice",
            instructions: "pick",
            criteria: { a: "A", no_match: "none" },
            requireNoMatch: true,
          },
        },
      },
      new AbortController().signal,
    );
    expect(judge).toHaveBeenCalledOnce();
    expect(outcome.providerCalled).toBe(true);
    expect(outcome.response.ok).toBe(true);
  });
});
