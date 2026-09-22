import { describe, expect, it } from "vitest";
import { hostedSessionPolicy, localOnlyPolicy } from "@relay/contracts";
import { MemoryArtifactStore } from "@relay/testkit";
import {
  createTypeSafeJudgmentPort,
  parseTypeSafeBody,
} from "../typesafe-judgment.js";
import { InMemoryGrantAccount } from "./grant-account.js";
import {
  evaluateHostedDisclosure,
  hashText,
  recordedHarnessGrant,
  resolveSealedSource,
} from "./hosted-grant.js";

const now = "2026-09-22T00:00:00.000Z";
const scope = { kind: "session" as const, id: "session_a" };
const question = { remember: { type: "noul" as const, instructions: "Remember?" } };
const request = {
  questionSetId: "judgment.remember",
  questionSetVersion: "1",
  model: "jev-latest",
  state: { token: "MSRP" },
  questions: question,
};

describe("Jev trust boundary", () => {
  it("rejects local-only disclosure and a relabel of the same bytes", async () => {
    const store = new MemoryArtifactStore();
    const secret = "local secret transcript";
    const sealed = await store.put(new TextEncoder().encode(secret), localOnlyPolicy());
    const relabeled = await store.put(new TextEncoder().encode(secret), hostedSessionPolicy());
    expect(relabeled.policy.disclosure).toBe("local_only");
    expect(relabeled.artifactId).toBe(sealed.artifactId);
    const source = await resolveSealedSource(store, {
      sourceClass: "ambient_transcript",
      field: "excerpt",
      artifactId: sealed.artifactId,
      sha256: sealed.sha256,
    });
    const decision = evaluateHostedDisclosure({
      grant: recordedHarnessGrant(scope.id),
      now,
      scope,
      requestsUsed: 0,
      bytesUsed: 0,
      sources: [{ ...source, ...(await hashText(source.text)) }],
      structuralState: { origin: "observed", excerpt: secret },
    });
    expect(decision.ok).toBe(false);
    if (!decision.ok) expect(decision.reason).toBe("local_only");
    expect(JSON.stringify(decision)).not.toContain(secret);
  });

  it("inherits the strictest policy for a derived artifact", async () => {
    const store = new MemoryArtifactStore();
    const parent = await store.put(new TextEncoder().encode("private parent"), localOnlyPolicy());
    const child = await store.put(new TextEncoder().encode("derived public wording"), hostedSessionPolicy(), [parent]);
    expect(child.policy.disclosure).toBe("local_only");
    const source = await resolveSealedSource(store, {
      sourceClass: "conversation_excerpt",
      field: "excerpt",
      artifactId: child.artifactId,
      sha256: child.sha256,
    });
    expect(source.policy.disclosure).toBe("local_only");
    expect(source.derivedFrom.some((row) => row.disclosure === "local_only")).toBe(true);
  });

  it("does not consume budget when the key is missing", async () => {
    const account = new InMemoryGrantAccount();
    const grant = recordedHarnessGrant(scope.id);
    await account.save({ ...grant, maxRequests: 2, maxBytes: 10_000 }, now);
    let reserved = 0;
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => null,
      fetchImpl: async () => {
        throw new Error("network");
      },
    });
    const result = await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            reserved += 1;
            return { ok: true, reservationId: "should-not-reserve" };
          },
          async commit() {
            reserved += 1;
          },
          async release() {
            reserved += 1;
          },
        },
      },
      new AbortController().signal,
    );
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.failure.transport?.configured).toBe(false);
      expect(result.failure.transport?.networkAttempted).toBe(false);
      expect(result.failure.transport?.attempts).toBe(0);
    }
    expect(reserved).toBe(0);
    expect((await account.read(scope)).requestsUsed).toBe(0);
  });

  it("cancels before any network attempt", async () => {
    let calls = 0;
    const controller = new AbortController();
    controller.abort();
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      fetchImpl: async () => {
        calls += 1;
        return new Response("{}", { status: 200 });
      },
    });
    const result = await port.judge(request, controller.signal);
    expect(calls).toBe(0);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.failure.category).toBe("cancelled");
      expect(result.failure.transport?.networkAttempted).toBe(false);
    }
  });

  it("times out a single attempt and stops when aborted during backoff", async () => {
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 1,
      attemptTimeoutMs: 20,
      fetchImpl: (_url, init) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () => {
            reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
          });
        }),
    });
    const timed = await port.judge(request, new AbortController().signal);
    expect(timed.ok).toBe(false);
    if (!timed.ok) {
      expect(timed.failure.category).toBe("timeout");
      expect(timed.failure.transport?.attempts).toBe(1);
      expect(timed.failure.transport?.networkAttempted).toBe(true);
    }

    let calls = 0;
    const controller = new AbortController();
    const cancelling = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 3,
      sleep: async () => {
        controller.abort();
      },
      fetchImpl: async () => {
        calls += 1;
        return new Response("{}", { status: 500 });
      },
    });
    const cancelled = await cancelling.judge(request, controller.signal);
    expect(calls).toBe(1);
    expect(cancelled.ok).toBe(false);
    if (!cancelled.ok) expect(cancelled.failure.category).toBe("cancelled");
  });

  it("retries 429 with Retry-After and 500, and reports the physical attempt count", async () => {
    let calls = 0;
    const slept: number[] = [];
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 3,
      random: () => 0,
      now: () => 1_000,
      sleep: async (ms) => {
        slept.push(ms);
      },
      fetchImpl: async () => {
        calls += 1;
        if (calls === 1) return new Response("{}", { status: 429, headers: { "retry-after": "1" } });
        if (calls === 2) return new Response("nope", { status: 500 });
        return new Response(
          JSON.stringify({ model: "jev-latest", answers: { remember: { type: "noul", noul: 0.4 } } }),
          { status: 200 },
        );
      },
    });
    const result = await port.judge(request, new AbortController().signal);
    expect(result.ok).toBe(true);
    expect(calls).toBe(3);
    expect(slept[0]).toBe(1_000);
    if (result.ok) {
      expect(result.success.transport?.attempts).toBe(3);
      expect(result.success.transport?.retryCount).toBe(2);
      expect(result.success.transport?.networkAttempted).toBe(true);
      expect(result.success.transport?.configured).toBe(true);
    }
  });

  it("rejects malformed JSON, duplicate keys, fractional scores, and tool text", () => {
    expect(parseTypeSafeBody("{", 1, question).ok).toBe(false);
    expect(
      parseTypeSafeBody(
        '{"model":"jev-latest","answers":{"remember":{"type":"noul","noul":0.2},"remember":{"type":"noul","noul":0.3}}}',
        1,
        question,
      ).ok,
    ).toBe(false);
    const fractional = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-latest",
        answers: {
          frustration: {
            type: "score",
            score: 1.5,
            legend: { "0": "Calm", "1": "Frustrated" },
            probabilities: { "0": 0.2, "1": 0.8 },
            confidence: 0.9,
          },
        },
      }),
      1,
      { frustration: { type: "score", instructions: "How frustrated?", criteria: ["Calm", "Frustrated"] } },
    );
    expect(fractional.ok).toBe(false);
    const tool = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-latest",
        answers: { remember: { type: "noul", noul: 0.2 } },
        tool_call: { name: "create_note", arguments: { text: "secret" } },
      }),
      1,
      question,
    );
    expect(tool.ok).toBe(false);
    expect(JSON.stringify(tool)).not.toContain("create_note");
  });

  it("stops concurrent consumers at the grant and releases a reservation that never committed", async () => {
    const account = new InMemoryGrantAccount();
    const grant = { ...recordedHarnessGrant(scope.id), grantId: "grant_race", maxRequests: 1, maxBytes: 100 };
    await account.save(grant, now);
    const [first, second] = await Promise.all([
      account.reserve({ grantId: grant.grantId, bytes: 10, now, reservationId: "a" }),
      account.reserve({ grantId: grant.grantId, bytes: 10, now, reservationId: "b" }),
    ]);
    const winners = [first, second].filter((item) => item.ok);
    expect(winners).toHaveLength(1);
    expect((await account.read(scope)).requestsUsed).toBe(1);
    await account.releaseUncommitted();
    expect((await account.read(scope)).requestsUsed).toBe(0);

    const again = await account.reserve({ grantId: grant.grantId, bytes: 10, now, reservationId: "c" });
    expect(again.ok).toBe(true);
    await account.commit("c");
    await account.releaseUncommitted();
    expect((await account.read(scope)).requestsUsed).toBe(1);
    const exhausted = await account.reserve({ grantId: grant.grantId, bytes: 10, now, reservationId: "d" });
    expect(exhausted).toEqual({ ok: false, reason: "exhausted" });
  });
});
