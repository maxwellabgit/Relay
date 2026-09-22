import {
  hostedSessionPolicy,
  isHostedEligible,
  localOnlyPolicy,
  type ArtifactProvenance,
  type ArtifactStorePort,
  type DataPolicy,
} from "@relay/contracts";
import type { EngineStore } from "../store.js";
import type { GrantAccount } from "./grant-account.js";

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

export type UnresolvedDisclosureSource = {
  readonly sourceClass: SourceClass;
  readonly field: "excerpt" | "contextExcerpt" | "excerpts";
  readonly artifactId: string;
  readonly sha256: string;
};

export type DisclosureSource = UnresolvedDisclosureSource & {
  readonly text: string;
  readonly policy: DataPolicy;
  readonly derivedFrom: ArtifactProvenance["derivedFrom"];
};

export type DisclosureReceipt = {
  readonly artifactIds: readonly string[];
  readonly decision: "allow" | DisclosureReason;
  readonly redactedBytes: number;
  readonly tokenEstimate: number;
  readonly grantId: string | null;
  readonly physicalAttempts: number;
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

/**
 * Policy for the first seal of a source. A later put cannot widen it.
 * Hosted session is used only when hosted processing is on and the grant allows the class.
 */
export async function initialDisclosureSeal(
  store: EngineStore,
  now: string,
  scope: DisclosureScope,
  sourceClass: SourceClass,
): Promise<DataPolicy> {
  try {
    if (!(await store.getHostedProcessingEnabled())) return localOnlyPolicy();
    const read = await new HostedGrantLedger(grantAccountFor(store)).read(scope);
    const grant = read.grant;
    if (!grant || grant.revokedAt) return localOnlyPolicy();
    if (Date.parse(now) >= Date.parse(grant.expiresAt)) return localOnlyPolicy();
    if (!grant.allowedSourceClasses.includes(sourceClass)) return localOnlyPolicy();
    return hostedSessionPolicy();
  } catch {
    return localOnlyPolicy();
  }
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
    if (source.policy.disclosure === "local_only" || !isHostedEligible(source.policy)) {
      return { ok: false, reason: "local_only" };
    }
    if (source.derivedFrom.some((item) => item.disclosure === "local_only")) {
      return { ok: false, reason: "revealing_derivative" };
    }
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
  constructor(private readonly account: GrantAccount) {}

  async save(grant: HostedJudgmentGrant, at: string): Promise<void> {
    await this.account.save(grant, at);
  }

  async revoke(grantId: string, at: string): Promise<void> {
    await this.account.revoke(grantId, at);
  }

  async consume(grantId: string, bytes: number, at: string): Promise<void> {
    const reservationId = `consume_${grantId}_${at}_${bytes}`;
    const reserved = await this.account.reserve({ grantId, bytes, now: at, reservationId });
    if (reserved.ok) await this.account.commit(reservationId);
  }

  findById(grantId: string): Promise<HostedJudgmentGrant | null> {
    return this.account.findById(grantId);
  }

  read(scope: DisclosureScope): Promise<{
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  }> {
    return this.account.read(scope);
  }

  reserve(input: { grantId: string; bytes: number; now: string; reservationId: string }) {
    return this.account.reserve(input);
  }

  commitReservation(reservationId: string): Promise<void> {
    return this.account.commit(reservationId);
  }

  releaseReservation(reservationId: string): Promise<void> {
    return this.account.release(reservationId);
  }
}

export function grantAccountFor(store: EngineStore): GrantAccount {
  if (
    store.saveHostedGrant &&
    store.revokeHostedGrant &&
    store.findHostedGrant &&
    store.readHostedGrant &&
    store.reserveHostedGrant &&
    store.commitHostedGrant &&
    store.releaseHostedGrant
  ) {
    return {
      save: (grant, at) => store.saveHostedGrant!(grant, at),
      revoke: (grantId, at) => store.revokeHostedGrant!(grantId, at),
      findById: (grantId) => store.findHostedGrant!(grantId),
      read: (scope) => store.readHostedGrant!(scope),
      reserve: (input) => store.reserveHostedGrant!(input),
      commit: (reservationId) => store.commitHostedGrant!(reservationId),
      release: (reservationId) => store.releaseHostedGrant!(reservationId),
      releaseUncommitted: () => store.releaseUncommittedHostedGrants?.() ?? Promise.resolve(0),
    };
  }
  throw new Error("hosted_grant_account_missing");
}

export async function sealedDisclosureInput(
  artifacts: ArtifactStorePort,
  input: {
    readonly text: string;
    readonly sourceClass: SourceClass;
    readonly field: DisclosureSource["field"];
  },
): Promise<UnresolvedDisclosureSource | null> {
  if (!input.text.trim()) return null;
  const ref = await artifacts.put(new TextEncoder().encode(input.text), hostedSessionPolicy());
  const provenance = await artifacts.provenance(ref.artifactId);
  if (!provenance || !isHostedEligible(provenance.policy)) return null;
  if (provenance.derivedFrom.some((row) => row.disclosure === "local_only")) return null;
  return {
    sourceClass: input.sourceClass,
    field: input.field,
    artifactId: ref.artifactId,
    sha256: ref.sha256,
  };
}

export async function resolveSealedSource(
  artifacts: ArtifactStorePort,
  source: UnresolvedDisclosureSource,
): Promise<DisclosureSource> {
  const provenance = await artifacts.provenance(source.artifactId);
  const sealed =
    provenance && provenance.sha256 === source.sha256
      ? provenance
      : {
          artifactId: source.artifactId,
          sha256: source.sha256,
          policy: localOnlyPolicy(),
          derivedFrom: [],
        };
  let text = "";
  try {
    const bytes = await artifacts.get({
      artifactId: source.artifactId,
      sha256: source.sha256,
      policy: sealed.policy,
    });
    const digest = await hashText(new TextDecoder().decode(bytes));
    if (digest.sha256 !== source.sha256) {
      return {
        ...source,
        text: "",
        policy: localOnlyPolicy(),
        derivedFrom: sealed.derivedFrom,
      };
    }
    text = new TextDecoder().decode(bytes);
  } catch {
    return { ...source, text: "", policy: localOnlyPolicy(), derivedFrom: sealed.derivedFrom };
  }
  return {
    ...source,
    text,
    policy: sealed.policy,
    derivedFrom: sealed.derivedFrom,
  };
}

export async function loadDisclosureGate(
  store: EngineStore,
  artifacts: ArtifactStorePort,
  now: string,
  scope: DisclosureScope,
  sources: readonly UnresolvedDisclosureSource[],
): Promise<{
  grant: HostedJudgmentGrant | null;
  now: string;
  scope: DisclosureScope;
  requestsUsed: number;
  bytesUsed: number;
  sources: readonly (DisclosureSource & DisclosedSource)[];
  commit: (bytes: number) => Promise<void>;
  attempt: {
    beforeAttempt(requestBytes: number): Promise<{ ok: true; reservationId: string } | { ok: false; reason: string }>;
    commit(reservationId: string): Promise<void>;
    release(reservationId: string): Promise<void>;
  };
}> {
  const ledger = new HostedGrantLedger(grantAccountFor(store));
  const read = await ledger.read(scope);
  const prepared = [];
  for (const source of sources) {
    const resolved = await resolveSealedSource(artifacts, source);
    const hashed = await hashText(resolved.text);
    prepared.push({ ...resolved, ...hashed });
  }
  return {
    ...read,
    now,
    scope,
    sources: prepared,
    commit: async (bytes: number) => {
      if (read.grant) await ledger.consume(read.grant.grantId, bytes, now);
    },
    attempt: {
      async beforeAttempt(requestBytes: number) {
        if (!read.grant) return { ok: false, reason: "missing" };
        const reservationId = `jev_${read.grant.grantId}_${prepared.length}_${requestBytes}_${Math.random().toString(16).slice(2)}`;
        const reserved = await ledger.reserve({
          grantId: read.grant.grantId,
          bytes: requestBytes,
          now,
          reservationId,
        });
        return reserved.ok ? { ok: true, reservationId } : { ok: false, reason: reserved.reason };
      },
      commit: (reservationId: string) => ledger.commitReservation(reservationId),
      release: (reservationId: string) => ledger.releaseReservation(reservationId),
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
