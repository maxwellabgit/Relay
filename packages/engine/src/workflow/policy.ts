import type { RawActivitySignal, StoredObservation, WorkflowEventType, WorkflowSettings } from "@relay/contracts";

export const MAX_SIGNAL_BYTES = 262_144;
export const MAX_EXCERPT = 8_000;
export const MAX_TITLE = 500;
export const MAX_URL = 2_000;

const EVENT_TYPES = new Set<WorkflowEventType>(["window.focus", "browser.navigate", "browser.activate", "page.semantic"]);

const SECRET_KEY = /password|passwd|secret|token|cookie|authorization|cvv|ssn|card|otp/i;
const PRIVATE_KEY = /BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY/;
const AUTH_SURFACE = /(password|sign-?in|login|oauth|checkout|payment|cvv|one-time code)/i;
const TOOL_LINE = /^\s*(tool|command|execute|sudo)\b/i;

export type PolicyContext = {
  readonly settings: WorkflowSettings;
  readonly sessionSites: readonly string[];
  readonly now: string;
};

export type PolicyDecision =
  | { readonly ok: true; readonly observation: Omit<StoredObservation, "observationId" | "permissionReceiptId" | "episodeId"> }
  | { readonly ok: false; readonly reason: string };

export function acceptSignal(signal: RawActivitySignal, context: PolicyContext): PolicyDecision {
  const encoded = JSON.stringify(signal);
  if (encoded.length > MAX_SIGNAL_BYTES) return { ok: false, reason: "oversized_observation" };
  if (!signal || typeof signal.observedAt !== "string" || Number.isNaN(Date.parse(signal.observedAt))) {
    return { ok: false, reason: "malformed_observation" };
  }
  if (signal.sourceType !== "browser" && signal.sourceType !== "desktop" && signal.sourceType !== "relay") {
    return { ok: false, reason: "malformed_observation" };
  }
  if (!EVENT_TYPES.has(signal.eventType as WorkflowEventType)) return { ok: false, reason: "malformed_observation" };
  if (ownKeys(signal).some((key) => SECRET_KEY.test(key))) return { ok: false, reason: "sensitive_blocked" };
  if (context.settings.paused) return { ok: false, reason: "observation_paused" };
  if (signal.sourceType === "desktop" && !context.settings.windowsEnabled) return { ok: false, reason: "source_disabled" };
  if (signal.sourceType === "browser" && !context.settings.chromeEnabled) return { ok: false, reason: "source_disabled" };

  const title = clean(signal.title, MAX_TITLE);
  const url = clean(signal.url, MAX_URL);
  const application = clean(signal.application, 120);
  let excerpt = clean(signal.excerpt, MAX_EXCERPT);
  if (excerpt && (PRIVATE_KEY.test(excerpt) || /password\s*[:=]/i.test(excerpt))) {
    return { ok: false, reason: "sensitive_blocked" };
  }
  excerpt = excerpt
    ?.split("\n")
    .filter((line) => !TOOL_LINE.test(line) && !/ignore (all|previous|above) instructions/i.test(line))
    .join("\n")
    .trim() || null;

  const host = hostname(url);
  const credential = AUTH_SURFACE.test(`${title ?? ""} ${url ?? ""}`);
  if (credential) excerpt = null;
  if (signal.eventType === "page.semantic") {
    if (!context.settings.pageContentEnabled || credential) {
      return { ok: false, reason: credential ? "sensitive_blocked" : "source_disabled" };
    }
    if (!host || !siteAllowed(host, context)) return { ok: false, reason: "site_denied" };
  }
  if (signal.sourceType === "browser" && host && !siteAllowed(host, context)) {
    return { ok: false, reason: "site_denied" };
  }
  if (signal.sourceType === "browser" && !host && signal.eventType !== "window.focus") {
    return { ok: false, reason: "malformed_observation" };
  }

  const retentionMs = context.settings.retentionDays * 24 * 60 * 60 * 1000;
  return {
    ok: true,
    observation: {
      observedAt: new Date(signal.observedAt).toISOString(),
      sourceType: signal.sourceType,
      provider: clean(signal.provider, 40) ?? signal.sourceType,
      deviceId: clean(signal.deviceId, 80) ?? "local",
      eventType: signal.eventType as WorkflowEventType,
      url,
      title,
      application,
      excerpt: signal.eventType === "page.semantic" ? excerpt : excerpt && context.settings.pageContentEnabled ? excerpt : null,
      permission: signal.permission ?? "metadata",
      durationMs: typeof signal.durationMs === "number" && signal.durationMs >= 0 ? Math.min(signal.durationMs, 86_400_000) : null,
      sensitivity: credential ? "credential_surface" : "ordinary",
      retentionUntil: new Date(Date.parse(context.now) + retentionMs).toISOString(),
    },
  };
}

export function siteAllowed(host: string, context: Pick<PolicyContext, "settings" | "sessionSites">): boolean {
  const name = host.toLowerCase();
  return [...context.settings.allowedSites, ...context.sessionSites].some((entry) => {
    const rule = entry.toLowerCase().replace(/^\*\./, ".");
    if (rule.startsWith(".")) return name.endsWith(rule) || name === rule.slice(1);
    return name === rule;
  });
}

export function hostname(url: string | null): string | null {
  if (!url) return null;
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return null;
  }
}

function clean(value: string | undefined, max: number): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.split("\u0000").join("").trim();
  if (!trimmed) return null;
  return trimmed.slice(0, max);
}

function ownKeys(value: object): string[] {
  return Object.keys(value);
}

/** Bridge messages are untrusted. Tool-shaped payloads never become observations. */
export function acceptBridgeEnvelope(
  raw: string,
): { ok: true; ping: true } | { ok: true; ping: false; signal: RawActivitySignal } | { ok: false; reason: string } {
  if (raw.length > MAX_SIGNAL_BYTES) return { ok: false, reason: "oversized_observation" };
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { ok: false, reason: "malformed_observation" };
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return { ok: false, reason: "malformed_observation" };
  const body = parsed as Record<string, unknown>;
  if (ownKeys(body).some((key) => SECRET_KEY.test(key) || key === "tool" || key === "command" || key === "execute")) {
    return { ok: false, reason: "sensitive_blocked" };
  }
  if (body.v !== 1) return { ok: false, reason: "malformed_observation" };
  if (body.kind === "ping") return { ok: true, ping: true };
  if (body.kind !== "observation") return { ok: false, reason: "malformed_observation" };
  if (typeof body.eventType !== "string" || !EVENT_TYPES.has(body.eventType as WorkflowEventType)) {
    return { ok: false, reason: "malformed_observation" };
  }
  return {
    ok: true,
    ping: false,
    signal: {
      observedAt: typeof body.observedAt === "string" ? body.observedAt : new Date().toISOString(),
      sourceType: "browser",
      provider: "chrome",
      eventType: body.eventType,
      ...(typeof body.url === "string" ? { url: body.url } : {}),
      ...(typeof body.title === "string" ? { title: body.title } : {}),
      ...(typeof body.application === "string" ? { application: body.application } : {}),
      ...(typeof body.excerpt === "string" ? { excerpt: body.excerpt } : {}),
      permission: body.permission === "always" || body.permission === "session" ? body.permission : "metadata",
    },
  };
}
