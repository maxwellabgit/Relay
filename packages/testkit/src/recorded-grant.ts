import { SOURCE_CLASSES, type HostedJudgmentGrant } from "@relay/engine";

/** Recorded-test grant. Production clients never receive this fixture. */
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
