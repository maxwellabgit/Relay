/**
 * V1 capability manifest — single source of truth for product claims,
 * UI visibility, tests, and adapter honesty.
 *
 * Status vocabulary (never use ambiguous "wired"):
 * - real: production path exists with durable side effects / receipts
 * - degraded: feature present but limited vs contract (honest UX required)
 * - test-only: harness / demo / recorded providers only — never claim as product
 * - not-shipped: hidden from production UI; post-V1 or blocked
 */
export const CAPABILITY_STATUSES = Object.freeze([
  "real",
  "degraded",
  "test-only",
  "not-shipped",
] as const);

export type CapabilityStatus = (typeof CAPABILITY_STATUSES)[number];

export const PLATFORM_IDS = Object.freeze(["windows", "ios", "android"] as const);
export type PlatformId = (typeof PLATFORM_IDS)[number];

export const V1_APPLICATION_IDS = Object.freeze({
  /** Expo iOS + Android application id (permanent). */
  mobile: "app.relay.assistant",
  /** Tauri desktop identifier (permanent). */
  desktop: "app.relay.desktop",
  /** Marketing version for store / installer surfaces. */
  marketingVersion: "1.0.0",
} as const);

export type CapabilityId =
  | "ask.typed"
  | "listen.foreground"
  | "model.local"
  | "model.mobile-tiny"
  | "jev.hosted"
  | "memory.local"
  | "note.capture"
  | "fact.capture"
  | "task.next-action"
  | "claim.verify"
  | "reflex.resolve-acronym"
  | "reflex.capture-note"
  | "reflex.remember-fact"
  | "reflex.recommend-next-action"
  | "tool.public-search"
  | "tool.github-read"
  | "connector.external-write"
  | "ambient.triage"
  | "diagnostics.live"
  | "dev.console"
  | "storage.sqlite"
  | "storage.protected-artifacts"
  | "secrets.platform";

export type CapabilityRow = {
  readonly id: CapabilityId;
  readonly title: string;
  readonly windows: CapabilityStatus;
  readonly ios: CapabilityStatus;
  readonly android: CapabilityStatus;
  readonly notes: string;
};

/**
 * Honest V1 matrix at finalization F1 freeze.
 * Update rows only when exact-SHA evidence changes the status.
 */
export const V1_CAPABILITY_MATRIX: readonly CapabilityRow[] = Object.freeze([
  {
    id: "ask.typed",
    title: "Typed Ask / chat",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop production path; mobile awaits createMobileClient (F2).",
  },
  {
    id: "listen.foreground",
    title: "Foreground listening",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Windows Listen exists when local ASR ready; journey 06 harness missing; iOS background audio not V1.",
  },
  {
    id: "model.local",
    title: "Desktop local text model",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "External loopback llama.cpp-compatible server; fails closed when absent.",
  },
  {
    id: "model.mobile-tiny",
    title: "On-device mobile-tiny model",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "F3 benchmark tournament required before selection.",
  },
  {
    id: "jev.hosted",
    title: "Hosted Jev judgments",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop TypeSafe + hosted_processing grant; mobile native transport is F2.",
  },
  {
    id: "memory.local",
    title: "Local memory search / recall",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Durable on desktop SQLite + artifacts.",
  },
  {
    id: "note.capture",
    title: "Note capture",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Ambient/local note paths exist; dedicated capture-note Reflex is F3.",
  },
  {
    id: "fact.capture",
    title: "Fact / memory capture",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Memory writes exist; remember-fact Reflex is F3.",
  },
  {
    id: "task.next-action",
    title: "Next-action recommendation",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Ambient triage can recommend; Reflex module is F3.",
  },
  {
    id: "claim.verify",
    title: "Claim verification",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Engine + Node proofs exist; production GitHub adapter must be real or hidden (F3).",
  },
  {
    id: "reflex.resolve-acronym",
    title: "Reflex: resolve-acronym@1",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Only complete production Reflex today.",
  },
  {
    id: "reflex.capture-note",
    title: "Reflex: capture-note",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "F3 reviewed module.",
  },
  {
    id: "reflex.remember-fact",
    title: "Reflex: remember-fact",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "F3 reviewed module.",
  },
  {
    id: "reflex.recommend-next-action",
    title: "Reflex: recommend-next-action",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "F3 reviewed module.",
  },
  {
    id: "tool.public-search",
    title: "Public search tool",
    windows: "test-only",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Hide from production UI until real adapter + receipts (F3).",
  },
  {
    id: "tool.github-read",
    title: "GitHub read tool",
    windows: "test-only",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Hide from production UI until real adapter + receipts (F3).",
  },
  {
    id: "connector.external-write",
    title: "External connector writes",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Never present success without provider receipt.",
  },
  {
    id: "ambient.triage",
    title: "Ambient triage / one recommendation",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop engine + UI cards; mobile composition F2.",
  },
  {
    id: "diagnostics.live",
    title: "Live structural diagnostics",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Correlation exists; live-summary unknowns and explain/export are F5.",
  },
  {
    id: "dev.console",
    title: "Developer console",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop only in production; mobile production must hide Dev (F4).",
  },
  {
    id: "storage.sqlite",
    title: "SQLite engine store",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Mobile expo-sqlite is F2.",
  },
  {
    id: "storage.protected-artifacts",
    title: "Protected content artifacts",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop DPAPI; mobile Keychain/Keystore encryption is F2.",
  },
  {
    id: "secrets.platform",
    title: "Platform secret store",
    windows: "real",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop DPAPI; mobile SecureStore + native Jev transport is F2.",
  },
]);

export function capabilityStatus(
  id: CapabilityId,
  platform: PlatformId,
): CapabilityStatus {
  const row = V1_CAPABILITY_MATRIX.find((entry) => entry.id === id);
  if (!row) {
    throw new Error(`unknown capability ${id}`);
  }
  return row[platform];
}

/** Capabilities that may be shown in production UI for a platform. */
export function visibleCapabilities(platform: PlatformId): readonly CapabilityId[] {
  return V1_CAPABILITY_MATRIX.filter(
    (row) => row[platform] === "real" || row[platform] === "degraded",
  ).map((row) => row.id);
}

/** Capabilities that must stay hidden on a platform (test-only or not-shipped). */
export function hiddenCapabilities(platform: PlatformId): readonly CapabilityId[] {
  return V1_CAPABILITY_MATRIX.filter(
    (row) => row[platform] === "test-only" || row[platform] === "not-shipped",
  ).map((row) => row.id);
}

export function assertCapabilityMatrixIntegrity(
  matrix: readonly CapabilityRow[] = V1_CAPABILITY_MATRIX,
): { ok: boolean; errors: string[] } {
  const errors: string[] = [];
  const seen = new Set<string>();
  for (const row of matrix) {
    if (seen.has(row.id)) errors.push(`duplicate capability id: ${row.id}`);
    seen.add(row.id);
    for (const platform of PLATFORM_IDS) {
      const status = row[platform];
      if (!CAPABILITY_STATUSES.includes(status)) {
        errors.push(`${row.id}.${platform} has invalid status ${String(status)}`);
      }
    }
    if (/\bwired\b/i.test(row.notes)) {
      errors.push(`${row.id} notes must not use ambiguous "wired"`);
    }
  }
  return { ok: errors.length === 0, errors };
}
