import { describe, expect, it, vi } from "vitest";
import type { ArtifactStorePort, DataPolicy } from "@relay/contracts";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import type { EngineStore } from "../store.js";
import {
  evaluateHostedDisclosure,
  hashText,
  HostedGrantLedger,
  recordedHarnessGrant,
  type DisclosureSource,
  type HostedJudgmentGrant,
} from "./hosted-grant.js";

const now = "2026-09-22T00:00:00.000Z";
const scope = { kind: "session" as const, id: "session_a" };

function grant(patch: Partial<HostedJudgmentGrant> = {}): HostedJudgmentGrant {
  return { ...recordedHarnessGrant(scope.id), ...patch };
}

async function source(
  text: string,
  patch: Partial<DisclosureSource> = {},
): Promise<DisclosureSource & { sha256: string; bytes: number }> {
  return {
    sourceClass: "ambient_transcript",
    field: "excerpt",
    text,
    ...patch,
    ...(await hashText(text)),
  };
}

function memoryStore(): Pick<EngineStore, "appendDomainEvent" | "listDomainEvents"> & {
  events: { sequence: number; type: string; at: string; payload: Record<string, unknown> }[];
} {
  const events: { sequence: number; type: string; at: string; payload: Record<string, unknown> }[] = [];
  return {
    events,
    async appendDomainEvent(type, at, payload) {
      events.push({ sequence: events.length + 1, type, at, payload });
      return events.length;
    },
    async listDomainEvents(limit) {
      return events.slice(-limit);
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

describe("hosted disclosure grant", () => {
  it("includes the authorized excerpt and drops neighboring prose", async () => {
    const authorized = "We promised to send the release report tomorrow.";
    const neighbor = "Unrelated payroll argument that must stay local.";
    const decision = evaluateHostedDisclosure({
      grant: grant(),
      now,
      scope,
      requestsUsed: 0,
      bytesUsed: 0,
      sources: [await source(authorized)],
      structuralState: { origin: "observed", excerpt: neighbor },
    });
    expect(decision.ok).toBe(true);
    if (!decision.ok) return;
    expect(JSON.stringify(decision.state)).toContain(authorized);
    expect(JSON.stringify(decision.state)).not.toContain(neighbor);
    expect(decision.disclosed[0]?.bytes).toBeGreaterThan(0);
    expect(decision.disclosed[0]?.sha256).toMatch(/^[0-9a-f]{64}$/);
  });

  it("refuses expired, revoked, wrong-scope, exhausted, denied, local-only, and derivative sources", async () => {
    const excerpt = await source("API means Active Pharmaceutical Ingredient here.");
    const base = {
      now,
      scope,
      requestsUsed: 0,
      bytesUsed: 0,
      sources: [excerpt],
      structuralState: { origin: "observed" },
    };
    expect(evaluateHostedDisclosure({ ...base, grant: null }).ok).toBe(false);
    expect(
      evaluateHostedDisclosure({ ...base, grant: grant({ expiresAt: "2020-01-01T00:00:00.000Z" }) }).ok,
    ).toBe(false);
    expect(
      evaluateHostedDisclosure({ ...base, grant: grant({ revokedAt: now }) }).ok,
    ).toBe(false);
    expect(
      evaluateHostedDisclosure({
        ...base,
        grant: grant(),
        scope: { kind: "project", id: "other" },
      }).ok,
    ).toBe(false);
    expect(evaluateHostedDisclosure({ ...base, grant: grant({ maxRequests: 1 }), requestsUsed: 1 }).ok).toBe(
      false,
    );
    expect(
      evaluateHostedDisclosure({
        ...base,
        grant: grant({ allowedSourceClasses: ["pattern_count"] }),
      }).ok,
    ).toBe(false);
    expect(
      evaluateHostedDisclosure({ ...base, grant: grant(), sources: [{ ...excerpt, localOnly: true }] }).ok,
    ).toBe(false);
    expect(
      evaluateHostedDisclosure({ ...base, grant: grant(), sources: [{ ...excerpt, revealsLocalOnly: true }] }).ok,
    ).toBe(false);
  });

  it("does not call the provider when a local-only source is disclosed", async () => {
    const judge = vi.fn(async () => {
      throw new Error("network");
    });
    const excerpt = await source("local secret transcript", { localOnly: true });
    const outcome = await runJudgmentLifecycle(
      {
        store: {
          async findCompletedJudgmentByHash() {
            return null;
          },
          async upsertJudgment() {
            return undefined;
          },
        } as unknown as EngineStore,
        artifacts: artifacts(),
        judgments: { judge },
        clock: { now: () => new Date(now) },
        ids: { next: (prefix) => `${prefix}_1` },
        isHostedProcessingAllowed: () => true,
        disclosure: {
          grant: grant(),
          now,
          scope,
          requestsUsed: 0,
          bytesUsed: 0,
          sources: [excerpt],
          commit: async () => undefined,
        },
      },
      {
        questionSetId: "judgment.ambient-triage",
        questionSetVersion: "1",
        model: "jev-latest",
        state: { origin: "observed", excerpt: excerpt.text },
        questions: {
          possible_commitment: { type: "noul", instructions: "Is this a commitment?" },
        },
      },
      new AbortController().signal,
    );
    expect(judge).not.toHaveBeenCalled();
    expect(outcome.providerCalled).toBe(false);
    expect(outcome.response.ok).toBe(false);
    if (!outcome.response.ok) expect(outcome.response.failure.message).toBe("disclosure_local_only");
  });

  it("tracks revocation and request budget in the ledger", async () => {
    const store = memoryStore();
    const ledger = new HostedGrantLedger(store);
    const saved = grant({ grantId: "grant_budget", maxRequests: 1 });
    await ledger.save(saved, now);
    await ledger.consume(saved.grantId, 12, now);
    const spent = await ledger.read(scope);
    expect(spent.requestsUsed).toBe(1);
    expect(spent.bytesUsed).toBe(12);
    await ledger.revoke(saved.grantId, now);
    const revoked = await ledger.read(scope);
    expect(revoked.grant?.revokedAt).toBe(now);
  });
});
