import type { DisclosureScope, HostedJudgmentGrant } from "./hosted-grant.js";

export type GrantReservation =
  | { readonly ok: true; readonly reservationId: string }
  | { readonly ok: false; readonly reason: "missing" | "expired" | "revoked" | "exhausted" };

export type GrantAccount = {
  save(grant: HostedJudgmentGrant, at: string): Promise<void>;
  revoke(grantId: string, at: string): Promise<void>;
  findById(grantId: string): Promise<HostedJudgmentGrant | null>;
  read(scope: DisclosureScope): Promise<{
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  }>;
  reserve(input: {
    grantId: string;
    bytes: number;
    now: string;
    reservationId: string;
  }): Promise<GrantReservation>;
  commit(reservationId: string): Promise<void>;
  release(reservationId: string): Promise<void>;
  releaseUncommitted(): Promise<number>;
};

type Row = HostedJudgmentGrant & {
  requestsCommitted: number;
  bytesCommitted: number;
};

type Hold = {
  grantId: string;
  bytes: number;
  state: "reserved" | "committed" | "released";
};

/**
 * Authoritative in-memory grant account. Overlapping reserves run one at a time
 * so two consumers cannot both pass a one-request budget.
 */
export class InMemoryGrantAccount implements GrantAccount {
  private readonly grants = new Map<string, Row>();
  private readonly holds = new Map<string, Hold>();
  private chain: Promise<unknown> = Promise.resolve();

  save(grant: HostedJudgmentGrant, at: string): Promise<void> {
    return this.exclusive(() => {
      void at;
      const current = this.grants.get(grant.grantId);
      const revokedAt = current?.revokedAt ?? grant.revokedAt;
      this.grants.set(grant.grantId, {
        grantId: grant.grantId,
        scopeKind: grant.scopeKind,
        scopeId: grant.scopeId,
        createdAt: grant.createdAt,
        expiresAt: grant.expiresAt,
        allowedSourceClasses: grant.allowedSourceClasses,
        maxRequests: grant.maxRequests,
        maxBytes: grant.maxBytes,
        ...(revokedAt ? { revokedAt } : {}),
        requestsCommitted: current?.requestsCommitted ?? 0,
        bytesCommitted: current?.bytesCommitted ?? 0,
      });
    });
  }

  revoke(grantId: string, at: string): Promise<void> {
    return this.exclusive(() => {
      const current = this.grants.get(grantId);
      if (current) this.grants.set(grantId, { ...current, revokedAt: at });
    });
  }

  findById(grantId: string): Promise<HostedJudgmentGrant | null> {
    return this.exclusive(() => {
      const row = this.grants.get(grantId);
      return row ? publicGrant(row) : null;
    });
  }

  read(scope: DisclosureScope): Promise<{
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  }> {
    return this.exclusive(() => this.readSync(scope));
  }

  reserve(input: {
    grantId: string;
    bytes: number;
    now: string;
    reservationId: string;
  }): Promise<GrantReservation> {
    return this.exclusive(() => {
      const row = this.grants.get(input.grantId);
      if (!row) return { ok: false, reason: "missing" };
      if (row.revokedAt) return { ok: false, reason: "revoked" };
      if (Date.parse(input.now) >= Date.parse(row.expiresAt)) return { ok: false, reason: "expired" };
      const held = this.held(input.grantId);
      if (row.requestsCommitted + held.requests + 1 > row.maxRequests) {
        return { ok: false, reason: "exhausted" };
      }
      if (row.bytesCommitted + held.bytes + input.bytes > row.maxBytes) {
        return { ok: false, reason: "exhausted" };
      }
      this.holds.set(input.reservationId, {
        grantId: input.grantId,
        bytes: input.bytes,
        state: "reserved",
      });
      return { ok: true, reservationId: input.reservationId };
    });
  }

  commit(reservationId: string): Promise<void> {
    return this.exclusive(() => {
      const hold = this.holds.get(reservationId);
      if (!hold || hold.state !== "reserved") return;
      const row = this.grants.get(hold.grantId);
      if (!row) return;
      this.grants.set(hold.grantId, {
        ...row,
        requestsCommitted: row.requestsCommitted + 1,
        bytesCommitted: row.bytesCommitted + hold.bytes,
      });
      this.holds.set(reservationId, { ...hold, state: "committed" });
    });
  }

  release(reservationId: string): Promise<void> {
    return this.exclusive(() => {
      const hold = this.holds.get(reservationId);
      if (!hold || hold.state !== "reserved") return;
      this.holds.set(reservationId, { ...hold, state: "released" });
    });
  }

  releaseUncommitted(): Promise<number> {
    return this.exclusive(() => {
      let released = 0;
      for (const [id, hold] of this.holds) {
        if (hold.state !== "reserved") continue;
        this.holds.set(id, { ...hold, state: "released" });
        released += 1;
      }
      return released;
    });
  }

  private readSync(scope: DisclosureScope): {
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  } {
    const matching = [...this.grants.values()].filter(
      (grant) => grant.scopeKind === scope.kind && grant.scopeId === scope.id,
    );
    const row = matching.at(-1) ?? null;
    if (!row) return { grant: null, requestsUsed: 0, bytesUsed: 0 };
    const held = this.held(row.grantId);
    return {
      grant: publicGrant(row),
      requestsUsed: row.requestsCommitted + held.requests,
      bytesUsed: row.bytesCommitted + held.bytes,
    };
  }

  private held(grantId: string): { requests: number; bytes: number } {
    let requests = 0;
    let bytes = 0;
    for (const hold of this.holds.values()) {
      if (hold.grantId !== grantId || hold.state !== "reserved") continue;
      requests += 1;
      bytes += hold.bytes;
    }
    return { requests, bytes };
  }

  private exclusive<T>(work: () => T): Promise<T> {
    const run = this.chain.then(() => work());
    this.chain = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }
}

function publicGrant(row: Row): HostedJudgmentGrant {
  return {
    grantId: row.grantId,
    scopeKind: row.scopeKind,
    scopeId: row.scopeId,
    createdAt: row.createdAt,
    expiresAt: row.expiresAt,
    allowedSourceClasses: row.allowedSourceClasses,
    maxRequests: row.maxRequests,
    maxBytes: row.maxBytes,
    ...(row.revokedAt ? { revokedAt: row.revokedAt } : {}),
  };
}
