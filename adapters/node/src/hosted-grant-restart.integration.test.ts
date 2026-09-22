import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { HostedJudgmentGrant } from "@relay/engine";
import { MemoryArtifactStore } from "./memory-artifacts.js";
import { openSqliteEngineStore } from "./sqlite-store.js";

const grant: HostedJudgmentGrant = {
  grantId: "grant_restart",
  scopeKind: "session",
  scopeId: "session_restart",
  createdAt: "2026-01-01T00:00:00.000Z",
  expiresAt: "2099-01-01T00:00:00.000Z",
  allowedSourceClasses: ["conversation_excerpt"],
  maxRequests: 2,
  maxBytes: 10_000,
};

describe("hosted grant restart", () => {
  it("releases an uncommitted reservation on reopen and keeps a committed attempt", async () => {
    const dir = await mkdtemp(join(tmpdir(), "relay-grant-"));
    const databasePath = join(dir, "relay.sqlite");
    try {
      const first = await openSqliteEngineStore(databasePath, new MemoryArtifactStore());
      await first.saveHostedGrant(grant, "2026-01-01T00:00:01.000Z");
      const committed = await first.reserveHostedGrant({
        grantId: grant.grantId,
        bytes: 20,
        now: "2026-01-01T00:00:02.000Z",
        reservationId: "res_committed",
      });
      expect(committed.ok).toBe(true);
      await first.commitHostedGrant("res_committed");
      const waiting = await first.reserveHostedGrant({
        grantId: grant.grantId,
        bytes: 20,
        now: "2026-01-01T00:00:03.000Z",
        reservationId: "res_waiting",
      });
      expect(waiting.ok).toBe(true);
      const before = await first.readHostedGrant({ kind: "session", id: "session_restart" });
      expect(before.requestsUsed).toBe(2);
      first.close();

      const reopened = await openSqliteEngineStore(databasePath, new MemoryArtifactStore());
      const after = await reopened.readHostedGrant({ kind: "session", id: "session_restart" });
      expect(after.requestsUsed).toBe(1);
      expect(after.bytesUsed).toBe(20);
      const again = await reopened.reserveHostedGrant({
        grantId: grant.grantId,
        bytes: 20,
        now: "2026-01-01T00:00:04.000Z",
        reservationId: "res_after_restart",
      });
      expect(again.ok).toBe(true);
      const exhausted = await reopened.reserveHostedGrant({
        grantId: grant.grantId,
        bytes: 20,
        now: "2026-01-01T00:00:05.000Z",
        reservationId: "res_over_cap",
      });
      expect(exhausted.ok).toBe(false);
      if (!exhausted.ok) expect(exhausted.reason).toBe("exhausted");
      reopened.close();
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
