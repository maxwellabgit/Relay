/**
 * V1 capability manifest — single source of truth for product claims,
 * UI visibility, tests, and adapter honesty.
 *
 * Status vocabulary (never use ambiguous "wired" or "complete"):
 * - shipped: exact-SHA headed or physical-device evidence exists for this platform
 * - degraded: feature present but limited vs contract (honest UX required)
 * - unverified-on-device: implementation exists; no headed or physical-device evidence yet
 * - not-shipped: hidden from production UI; stub, placeholder, or out of V1
 */
export const CAPABILITY_STATUSES = Object.freeze([
  "shipped",
  "degraded",
  "unverified-on-device",
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
 * Honest V1 matrix at reviewed SHA 1862daacc8d06c6bc367c85b4cd523779d99b8fa.
 * Nothing is `shipped`: no capability has headed-Windows or physical-device
 * evidence at this SHA. Update a row only when exact-SHA evidence changes it.
 */
export const V1_CAPABILITY_MATRIX: readonly CapabilityRow[] = Object.freeze([
  {
    id: "ask.typed",
    title: "Typed Ask / chat",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop path exists; boot/error surfaces and headed journeys 02-12 are open. The app holds the composer draft, the in-flight send, and the thread offset across a view remount. Mobile local model is still mobile_model_pending.",
  },
  {
    id: "listen.foreground",
    title: "Foreground listening",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Windows Listen fails closed without local ASR. Mobile listening stays off while speech is unavailable. Background, interruption, route change, cancel, and relaunch leave listening off. A prepared audio_file segment list can be replayed with listening left off. No measured audio level is shown. No speech package is pinned. No physical-device proof.",
  },
  {
    id: "model.local",
    title: "Desktop local text model",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "External loopback llama.cpp-compatible server; fails closed when absent. An invalid typed draft, search query, and direct answer are each repaired once. Not verified on a headed Windows release profile.",
  },
  {
    id: "model.mobile-tiny",
    title: "On-device mobile-tiny model",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "No tested model is selected, so download stays off. A delivery controller can pause, cancel, verify a hash, and delete a local copy when a pin exists. Device tournament has not selected a runtime.",
  },
  {
    id: "jev.hosted",
    title: "Hosted Jev judgments",
    windows: "degraded",
    ios: "degraded",
    android: "degraded",
    notes: "Transport, jev-latest, a scoped disclosure grant, a two-round cap, and one durable resume exist in code. Judgment events keep the grant id, scope, expiry, budget before and after, disclosed byte count, HTTP status, retry delay, and provider request id when one is returned. Mobile uses that typed port. The unused pending transport is removed. Live canary has not run.",
  },
  {
    id: "memory.local",
    title: "Local memory search / recall",
    windows: "unverified-on-device",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Desktop SQLite plus artifacts pass lower-layer tests. No headed relaunch proof at this SHA.",
  },
  {
    id: "note.capture",
    title: "Note capture",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit note phrases store kind note. Headed persistence proof is open.",
  },
  {
    id: "fact.capture",
    title: "Fact / memory capture",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit fact phrases store kind fact, separate from notes. Headed proof is open.",
  },
  {
    id: "task.next-action",
    title: "Next-action recommendation",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit next-action phrases store kind recommendation. Ambient cards stay reviewable until accept. Headed proof is open.",
  },
  {
    id: "claim.verify",
    title: "Claim verification",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Engine proofs exist. Production GitHub adapter stays hidden until receipts exist.",
  },
  {
    id: "reflex.resolve-acronym",
    title: "Reflex: resolve-acronym@1",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Module is registered and the choice request includes an authorized excerpt. Live canary has not run.",
  },
  {
    id: "reflex.capture-note",
    title: "Reflex: capture-note",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit-phrase module exists. Headed persistence proof is open.",
  },
  {
    id: "reflex.remember-fact",
    title: "Reflex: remember-fact",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit-phrase module exists. Headed fact-versus-note proof is open.",
  },
  {
    id: "reflex.recommend-next-action",
    title: "Reflex: recommend-next-action",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Explicit-phrase module exists. Reviewable recommendation proof is open.",
  },
  {
    id: "tool.public-search",
    title: "Public search tool",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Hidden. No production adapter or receipts.",
  },
  {
    id: "tool.github-read",
    title: "GitHub read tool",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Hidden. No production adapter or receipts.",
  },
  {
    id: "connector.external-write",
    title: "External connector writes",
    windows: "not-shipped",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Never present success without a provider receipt.",
  },
  {
    id: "ambient.triage",
    title: "Ambient triage / one recommendation",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Observed speech stages a review card and does not write memory until accept. Authorized excerpt is included when a grant allows it. Live canary has not run.",
  },
  {
    id: "diagnostics.live",
    title: "Live structural diagnostics",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Event-derived summary includes grant and attempt fields when a judgment runs. Mobile trace is a bounded device file. Share diagnostics omits source transcripts. No physical-device export proof.",
  },
  {
    id: "dev.console",
    title: "Developer console",
    windows: "degraded",
    ios: "not-shipped",
    android: "not-shipped",
    notes: "Shown only when the build channel is internal and the console flag is set. A production channel stays hidden. Visual and device proof is still open.",
  },
  {
    id: "storage.sqlite",
    title: "SQLite engine store",
    windows: "unverified-on-device",
    ios: "unverified-on-device",
    android: "unverified-on-device",
    notes: "Desktop and mobile composition code exist. Physical relaunch evidence does not.",
  },
  {
    id: "storage.protected-artifacts",
    title: "Protected content artifacts",
    windows: "unverified-on-device",
    ios: "unverified-on-device",
    android: "unverified-on-device",
    notes: "Desktop DPAPI and mobile document-file code exist. Device encrypt/relaunch/decrypt evidence does not.",
  },
  {
    id: "secrets.platform",
    title: "Platform secret store",
    windows: "degraded",
    ios: "unverified-on-device",
    android: "unverified-on-device",
    notes: "Desktop DPAPI and mobile SecureStore code exist. Windows file import and physical SecureStore proof are open.",
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
  return V1_CAPABILITY_MATRIX.filter((row) => {
    const status = row[platform];
    return status === "shipped" || status === "degraded" || status === "unverified-on-device";
  }).map((row) => row.id);
}

/** Capabilities that must stay hidden on a platform. */
export function hiddenCapabilities(platform: PlatformId): readonly CapabilityId[] {
  return V1_CAPABILITY_MATRIX.filter((row) => row[platform] === "not-shipped").map((row) => row.id);
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
    if (/\bwired\b/i.test(row.notes) || /\bcomplete\b/i.test(row.notes)) {
      errors.push(`${row.id} notes must not use ambiguous "wired" or "complete"`);
    }
  }
  return { ok: errors.length === 0, errors };
}
