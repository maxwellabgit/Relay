export type ProductSurface =
  | "initializing"
  | "ready"
  | "busy"
  | "waiting"
  | "degraded"
  | "failed"
  | "retrying";

export function productSurface(input: {
  readonly phase: "booting" | "failed" | "live";
  readonly busy: boolean;
  readonly queueDepth: number;
  readonly waitCount: number;
  readonly engineOk: boolean | null;
  readonly retrying: boolean;
}): ProductSurface {
  if (input.phase === "failed") return "failed";
  if (input.phase === "booting") return "initializing";
  if (input.engineOk === false) return "failed";
  if (input.retrying) return "retrying";
  if (input.waitCount > 0) return "waiting";
  if (input.busy || input.queueDepth > 0) return "busy";
  if (input.engineOk === null) return "degraded";
  return "ready";
}

export function isRetrying(
  trace: readonly { readonly status: string | null; readonly attempt?: number | null }[],
): boolean {
  const latest = trace.at(-1);
  return Boolean(latest && latest.status === "waiting" && (latest.attempt ?? 0) > 1);
}

const CORE_STATUS_IDS = new Set(["engine", "storage"]);

/** Core health stays separate from Jev. Halo is not a V1 capability and is not a failure. */
export function coreHealth(
  status: readonly { readonly id: string; readonly ok: boolean; readonly label: string }[],
  providers: readonly { readonly ok: boolean; readonly status: string }[],
): { readonly level: "ok" | "warn" | "bad"; readonly label: string } {
  const core = status.find((item) => !item.ok && CORE_STATUS_IDS.has(item.id));
  if (core) return { level: "bad", label: core.label };
  const optional = status.find((item) => !item.ok && !CORE_STATUS_IDS.has(item.id));
  if (optional) return { level: "warn", label: optional.label };
  const provider = providers.find((item) => !item.ok);
  if (provider) return { level: "warn", label: provider.status };
  if (status.length === 0 && providers.length === 0) return { level: "warn", label: "starting" };
  return { level: "ok", label: "healthy" };
}

const NOTICES: Record<string, string> = {
  client_not_ready: "RELAY is still starting.",
  start_failed: "RELAY could not start.",
  client_failed: "RELAY could not start.",
  command_failed: "That did not go through.",
  action_failed: "That did not go through.",
  replay_failed: "Replay is unavailable.",
  audio_unavailable: "Listening is unavailable.",
  speech_unavailable: "Speech is not available on this device.",
  permission_denied: "Microphone permission is off.",
  interruption: "Listening stopped because audio was interrupted.",
  route_change: "Listening stopped because the audio route changed.",
  background: "Listening stays off in the background.",
  cancel: "Listening stopped.",
  max_semantic_rounds: "RELAY stopped after two judgment rounds.",
  missing_secret: "Hosted judgment needs a key in Settings.",
  not_running: "RELAY is not running yet.",
  scope_required: "Choose a session or project before granting.",
  wrong_scope: "That grant does not match this session.",
  grant_missing: "There is no grant to revoke.",
  already_revoked: "That grant is already revoked.",
  dev_console_disabled: "The developer console is off.",
  not_authorized: "Hosted judgment is not authorized.",
  disabled: "Hosted processing is off.",
  authentication: "The hosted key was rejected.",
};

/** Consumer copy for a machine code. Sentences stay as written. */
export function humanNotice(value: string): string {
  const known = NOTICES[value];
  if (known) return known;
  if (/^[a-z0-9_]+$/.test(value)) return "Something went wrong. Details are in diagnostics.";
  return value;
}

const SPEECH_HOLD = new Set(["permission_denied", "interruption", "route_change", "background", "cancel"]);

/** Copy for a down audio chip, or for a suspend reason while the chip is still capable. */
export function speechHoldCopy(ok: boolean, detail: string): string | null {
  if (!ok || SPEECH_HOLD.has(detail)) return humanNotice(detail);
  return null;
}

export function humanWait(waitKind: string): string {
  if (waitKind === "hosted_judgment") return "Waiting for a judgment.";
  if (waitKind === "judgment") return "Waiting.";
  if (/^[a-z0-9_]+$/.test(waitKind)) return "Waiting.";
  return waitKind;
}

export function surfaceCopy(surface: ProductSurface): string | null {
  switch (surface) {
    case "initializing":
      return "Starting RELAY…";
    case "failed":
      return "RELAY is not running.";
    case "retrying":
      return "Retrying.";
    case "waiting":
      return "Waiting.";
    case "busy":
      return "Working.";
    case "degraded":
      return "Limited.";
    case "ready":
      return null;
  }
}
