import { describe, expect, it, vi } from "vitest";
import type { ArtifactStorePort, DataPolicy } from "@relay/contracts";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import type { EngineStore } from "../store.js";
import { claimSemanticRound } from "./semantic-rounds.js";

const now = "2026-09-22T00:00:00.000Z";

function memoryStore(): Pick<EngineStore, "appendDomainEvent" | "listDomainEvents" | "findCompletedJudgmentByHash" | "upsertJudgment"> {
  const events: { sequence: number; type: string; at: string; payload: Record<string, unknown> }[] = [];
  return {
    async appendDomainEvent(type, at, payload) {
      events.push({ sequence: events.length + 1, type, at, payload });
      return events.length;
    },
    async listDomainEvents(limit) {
      return events.slice(-limit);
    },
    async findCompletedJudgmentByHash() {
      return null;
    },
    async upsertJudgment() {
      return undefined;
    },
  };
}

function artifacts(): ArtifactStorePort {
  const blobs = new Map<string, Uint8Array>();
  let n = 0;
  return {
    async put(bytes: Uint8Array, _policy: DataPolicy) {
      void _policy;
      const artifactId = `art_${++n}`;
      const digest = await crypto.subtle.digest("SHA-256", bytes);
      const sha256 = [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, "0")).join("");
      blobs.set(`${artifactId}:${sha256}`, bytes);
      return { artifactId, sha256, policy: { disclosure: "local_only", sensitivity: 0 } };
    },
    async get(ref) {
      const bytes = blobs.get(`${ref.artifactId}:${ref.sha256}`);
      if (!bytes) throw new Error("missing");
      return bytes;
    },
  };
}

describe("semantic rounds", () => {
  it("allows a repeated question and a second round, then refuses a third", async () => {
    const store = memoryStore();
    const first = await claimSemanticRound(store, "case_1", "judgment.acronym-choice", now);
    const retry = await claimSemanticRound(store, "case_1", "judgment.acronym-choice", now);
    const second = await claimSemanticRound(store, "case_1", "judgment.tool-route", now);
    const third = await claimSemanticRound(store, "case_1", "judgment.claim-source", now);
    expect(first).toEqual({ ok: true, round: 1 });
    expect(retry).toEqual({ ok: true, round: 1 });
    expect(second).toEqual({ ok: true, round: 2 });
    expect(third).toEqual({ ok: false, reason: "max_semantic_rounds" });
  });

  it("does not call the provider for the third semantic round", async () => {
    const store = memoryStore();
    const judge = vi.fn(async () => ({
      ok: true as const,
      success: {
        model: "jev-latest",
        answers: { expansion: { type: "noul" as const, probabilityYes: 0.8 } },
        inputTokens: 1,
        outputTokens: 1,
        elapsedMs: 1,
      },
    }));
    const run = (questionSetId: string) =>
      runJudgmentLifecycle(
        {
          store: store as unknown as EngineStore,
          artifacts: artifacts(),
          judgments: { judge },
          clock: { now: () => new Date(now) },
          ids: { next: (prefix) => `${prefix}_1` },
          isHostedProcessingAllowed: () => true,
        },
        {
          questionSetId,
          questionSetVersion: "1",
          model: "jev-latest",
          caseId: "case_rounds",
          state: { origin: "observed" },
          questions: { expansion: { type: "noul", instructions: "Is this useful?" } },
        },
        new AbortController().signal,
      );
    expect((await run("judgment.one")).providerCalled).toBe(true);
    expect((await run("judgment.two")).providerCalled).toBe(true);
    const refused = await run("judgment.three");
    expect(refused.providerCalled).toBe(false);
    expect(judge).toHaveBeenCalledTimes(2);
    expect(refused.response.ok).toBe(false);
    if (!refused.response.ok) expect(refused.response.failure.message).toBe("max_semantic_rounds");
  });
});
