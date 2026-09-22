import type { EngineStore } from "../store.js";

export const SOURCE_CLASSES = [
  "conversation_excerpt",
  "ambient_transcript",
  "claim_excerpt",
  "pattern_count",
] as const;

export type SourceClass = (typeof SOURCE_CLASSES)[number];

export type DisclosureScope = {
  readonly kind: "session" | "project";
  readonly id: string;
};

export type HostedJudgmentGrant = {
  readonly grantId: string;
  readonly scopeKind: DisclosureScope["kind"];
  readonly scopeId: string;
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly allowedSourceClasses: readonly SourceClass[];
  readonly maxRequests: number;
  readonly maxBytes: number;
  readonly revokedAt?: string;
};

export type DisclosureSource = {
  readonly sourceClass: SourceClass;
  readonly field: "excerpt" | "contextExcerpt" | "excerpts";
  readonly text: string;
  readonly localOnly?: boolean;
  readonly revealsLocalOnly?: boolean;
};

export type DisclosedSource = {
  readonly sourceClass: SourceClass;
  readonly sha256: string;
  readonly bytes: number;
};

export type DisclosureReason =
  | "missing"
  | "expired"
  | "revoked"
  | "wrong_scope"
  | "exhausted"
  | "source_denied"
  | "local_only"
  | "revealing_derivative";

export type DisclosureDecision =
  | {
      readonly ok: true;
      readonly grantId: string;
      readonly bytes: number;
      readonly state: Record<string, unknown>;
      readonly disclosed: readonly DisclosedSource[];
    }
  | { readonly ok: false; readonly reason: DisclosureReason };

const GRANTED = "jev.disclosure_granted";
const REVOKED = "jev.disclosure_revoked";
const CONSUMED = "jev.disclosure_consumed";

export const JEV_GRANT_LIMITS = {
  minTtlMs: 60_000,
  maxTtlMs: 24 * 60 * 60 * 1000,
  minRequests: 1,
  maxRequests: 50,
  minBytes: 1,
  maxBytes: 200_000,
} as const;

export function buildHostedJudgmentGrant(input: {
  readonly grantId: string;
  readonly scopeKind: DisclosureScope["kind"];
  readonly scopeId: string;
  readonly now: string;
  readonly ttlMs: number;
  readonly allowedSourceClasses: readonly string[];
  readonly maxRequests: number;
  readonly maxBytes: number;
}): { ok: true; grant: HostedJudgmentGrant } | { ok: false; reason: string } {
  if (!input.scopeId) return { ok: false, reason: "scope_required" };
  if (!Number.isFinite(Date.parse(input.now))) return { ok: false, reason: "ttl_out_of_range" };
  if (
    !Number.isFinite(input.ttlMs) ||
    input.ttlMs < JEV_GRANT_LIMITS.minTtlMs ||
    input.ttlMs > JEV_GRANT_LIMITS.maxTtlMs
  ) {
    return { ok: false, reason: "ttl_out_of_range" };
  }
  if (
    !Number.isInteger(input.maxRequests) ||
    input.maxRequests < JEV_GRANT_LIMITS.minRequests ||
    input.maxRequests > JEV_GRANT_LIMITS.maxRequests ||
    !Number.isInteger(input.maxBytes) ||
    input.maxBytes < JEV_GRANT_LIMITS.minBytes ||
    input.maxBytes > JEV_GRANT_LIMITS.maxBytes
  ) {
    return { ok: false, reason: "budget_out_of_range" };
  }
  if (input.allowedSourceClasses.length === 0) return { ok: false, reason: "source_class_invalid" };
  const allowed: SourceClass[] = [];
  for (const item of input.allowedSourceClasses) {
    if (!SOURCE_CLASSES.includes(item as SourceClass) || allowed.includes(item as SourceClass)) {
      return { ok: false, reason: "source_class_invalid" };
    }
    allowed.push(item as SourceClass);
  }
  return {
    ok: true,
    grant: {
      grantId: input.grantId,
      scopeKind: input.scopeKind,
      scopeId: input.scopeId,
      createdAt: input.now,
      expiresAt: new Date(Date.parse(input.now) + input.ttlMs).toISOString(),
      allowedSourceClasses: allowed,
      maxRequests: input.maxRequests,
      maxBytes: input.maxBytes,
    },
  };
}

export function recordedHarnessGrant(scopeId: string): HostedJudgmentGrant {
  return {
    grantId: "grant_recorded_harness",
    scopeKind: "session",
    scopeId,
    createdAt: "2026-01-01T00:00:00.000Z",
    expiresAt: "2099-01-01T00:00:00.000Z",
    allowedSourceClasses: [...SOURCE_CLASSES],
    maxRequests: 1000,
    maxBytes: 1_000_000,
  };
}

export function evaluateHostedDisclosure(input: {
  readonly grant: HostedJudgmentGrant | null;
  readonly now: string;
  readonly scope: DisclosureScope;
  readonly requestsUsed: number;
  readonly bytesUsed: number;
  readonly sources: readonly (DisclosureSource & DisclosedSource)[];
  readonly structuralState: unknown;
}): DisclosureDecision {
  const grant = input.grant;
  if (!grant) return { ok: false, reason: "missing" };
  if (grant.revokedAt) return { ok: false, reason: "revoked" };
  if (Date.parse(input.now) >= Date.parse(grant.expiresAt)) return { ok: false, reason: "expired" };
  if (grant.scopeKind !== input.scope.kind || grant.scopeId !== input.scope.id) {
    return { ok: false, reason: "wrong_scope" };
  }
  const bytes = input.sources.reduce((total, source) => total + source.bytes, 0);
  if (input.requestsUsed >= grant.maxRequests || input.bytesUsed + bytes > grant.maxBytes) {
    return { ok: false, reason: "exhausted" };
  }
  for (const source of input.sources) {
    if (source.localOnly) return { ok: false, reason: "local_only" };
    if (source.revealsLocalOnly) return { ok: false, reason: "revealing_derivative" };
    if (!grant.allowedSourceClasses.includes(source.sourceClass)) {
      return { ok: false, reason: "source_denied" };
    }
  }
  return {
    ok: true,
    grantId: grant.grantId,
    bytes,
    state: providerState(input.structuralState, input.sources),
    disclosed: input.sources.map((source) => ({
      sourceClass: source.sourceClass,
      sha256: source.sha256,
      bytes: source.bytes,
    })),
  };
}

export class HostedGrantLedger {
  constructor(
    private readonly store: Pick<EngineStore, "appendDomainEvent" | "listDomainEvents">,
  ) {}

  async save(grant: HostedJudgmentGrant, at: string): Promise<void> {
    await this.store.appendDomainEvent(GRANTED, at, { grant });
  }

  async revoke(grantId: string, at: string): Promise<void> {
    await this.store.appendDomainEvent(REVOKED, at, { grantId });
  }

  async consume(grantId: string, bytes: number, at: string): Promise<void> {
    await this.store.appendDomainEvent(CONSUMED, at, { grantId, bytes });
  }

  async findById(grantId: string): Promise<HostedJudgmentGrant | null> {
    const events = await this.store.listDomainEvents(8000);
    let saved: HostedJudgmentGrant | null = null;
    let revokedAt: string | undefined;
    for (const event of events) {
      if (event.type === GRANTED) {
        const parsed = parseGrant(event.payload.grant);
        if (parsed?.grantId === grantId) {
          saved = parsed;
          revokedAt = undefined;
        }
      } else if (event.type === REVOKED && event.payload.grantId === grantId) {
        revokedAt = event.at;
      }
    }
    if (!saved) return null;
    return revokedAt ? { ...saved, revokedAt } : saved;
  }

  async read(scope: DisclosureScope): Promise<{
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  }> {
    const events = await this.store.listDomainEvents(8000);
    const grants = new Map<string, HostedJudgmentGrant>();
    const usage = new Map<string, { requests: number; bytes: number }>();
    for (const event of events) {
      if (event.type === GRANTED) {
        const grant = parseGrant(event.payload.grant);
        if (grant) grants.set(grant.grantId, grant);
      } else if (event.type === REVOKED && typeof event.payload.grantId === "string") {
        const current = grants.get(event.payload.grantId);
        if (current) grants.set(current.grantId, { ...current, revokedAt: event.at });
      } else if (event.type === CONSUMED && typeof event.payload.grantId === "string") {
        const bytes = typeof event.payload.bytes === "number" ? event.payload.bytes : 0;
        const current = usage.get(event.payload.grantId) ?? { requests: 0, bytes: 0 };
        usage.set(event.payload.grantId, {
          requests: current.requests + 1,
          bytes: current.bytes + bytes,
        });
      }
    }
    const matching = [...grants.values()].filter(
      (grant) => grant.scopeKind === scope.kind && grant.scopeId === scope.id,
    );
    const grant = matching.at(-1) ?? null;
    const spent = grant ? usage.get(grant.grantId) : undefined;
    return {
      grant,
      requestsUsed: spent?.requests ?? 0,
      bytesUsed: spent?.bytes ?? 0,
    };
  }
}

export async function loadDisclosureGate(
  store: Pick<EngineStore, "appendDomainEvent" | "listDomainEvents">,
  now: string,
  scope: DisclosureScope,
  sources: readonly DisclosureSource[],
): Promise<{
  grant: HostedJudgmentGrant | null;
  now: string;
  scope: DisclosureScope;
  requestsUsed: number;
  bytesUsed: number;
  sources: readonly (DisclosureSource & DisclosedSource)[];
  commit: (bytes: number) => Promise<void>;
}> {
  const ledger = new HostedGrantLedger(store);
  const read = await ledger.read(scope);
  const prepared = [];
  for (const source of sources) {
    const hashed = await hashText(source.text);
    prepared.push({ ...source, ...hashed });
  }
  return {
    ...read,
    now,
    scope,
    sources: prepared,
    commit: async (bytes: number) => {
      if (read.grant) await ledger.consume(read.grant.grantId, bytes, now);
    },
  };
}

export function sessionDisclosureView(
  read: { grant: HostedJudgmentGrant | null; requestsUsed: number; bytesUsed: number },
  now: string,
): {
  readonly grantId: string;
  readonly scopeKind: DisclosureScope["kind"];
  readonly scopeId: string;
  readonly expiresAt: string;
  readonly requestsUsed: number;
  readonly maxRequests: number;
  readonly bytesUsed: number;
  readonly maxBytes: number;
  readonly allowedSourceClasses: readonly SourceClass[];
} | null {
  const grant = read.grant;
  if (!grant || grant.revokedAt) return null;
  if (Date.parse(now) >= Date.parse(grant.expiresAt)) return null;
  return {
    grantId: grant.grantId,
    scopeKind: grant.scopeKind,
    scopeId: grant.scopeId,
    expiresAt: grant.expiresAt,
    requestsUsed: read.requestsUsed,
    maxRequests: grant.maxRequests,
    bytesUsed: read.bytesUsed,
    maxBytes: grant.maxBytes,
    allowedSourceClasses: grant.allowedSourceClasses,
  };
}

export async function hashText(text: string): Promise<{ sha256: string; bytes: number }> {
  const bytes = new TextEncoder().encode(text);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return {
    sha256: [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, "0")).join(""),
    bytes: bytes.byteLength,
  };
}

function providerState(
  structuralState: unknown,
  sources: readonly (DisclosureSource & DisclosedSource)[],
): Record<string, unknown> {
  const state: Record<string, unknown> = {};
  if (structuralState && typeof structuralState === "object" && !Array.isArray(structuralState)) {
    for (const [key, value] of Object.entries(structuralState as Record<string, unknown>)) {
      if (key === "excerpt" || key === "contextExcerpt" || key === "excerpts") continue;
      if (typeof value === "string" && (value.length > 64 || /\s/.test(value))) continue;
      state[key] = value;
    }
  }
  const excerpts: string[] = [];
  for (const source of sources) {
    if (source.field === "excerpts") excerpts.push(source.text);
    else state[source.field] = source.text;
  }
  if (excerpts.length > 0) state.excerpts = excerpts;
  return state;
}

function parseGrant(value: unknown): HostedJudgmentGrant | null {
  if (!value || typeof value !== "object") return null;
  const row = value as Partial<HostedJudgmentGrant>;
  if (
    typeof row.grantId !== "string" ||
    (row.scopeKind !== "session" && row.scopeKind !== "project") ||
    typeof row.scopeId !== "string" ||
    typeof row.createdAt !== "string" ||
    typeof row.expiresAt !== "string" ||
    !Array.isArray(row.allowedSourceClasses) ||
    typeof row.maxRequests !== "number" ||
    typeof row.maxBytes !== "number"
  ) {
    return null;
  }
  const allowed = row.allowedSourceClasses.filter((item): item is SourceClass =>
    SOURCE_CLASSES.includes(item as SourceClass),
  );
  return {
    grantId: row.grantId,
    scopeKind: row.scopeKind,
    scopeId: row.scopeId,
    createdAt: row.createdAt,
    expiresAt: row.expiresAt,
    allowedSourceClasses: allowed,
    maxRequests: row.maxRequests,
    maxBytes: row.maxBytes,
    ...(typeof row.revokedAt === "string" ? { revokedAt: row.revokedAt } : {}),
  };
}
