import type { ActivitySettings, Observation } from "@relay/contracts";
import { normalizeDomain } from "./domains.js";

export type PolicyDecision =
  | { readonly ok: true; readonly observation: Observation; readonly deniedDomain: boolean }
  | { readonly ok: false; readonly reason: string };

function permitted(settings: ActivitySettings, domain: string | undefined): boolean {
  if (!domain) return false;
  return settings.permittedDomains.includes(domain);
}

export function applyObservationPolicy(observation: Observation, settings: ActivitySettings): PolicyDecision {
  if (!settings.enabled) return { ok: false, reason: "observation_off" };
  if (observation.source.type === "windows" && !settings.windowsEnabled) {
    return { ok: false, reason: "windows_off" };
  }
  if (observation.source.type === "chrome" && !settings.chromeEnabled) {
    return { ok: false, reason: "chrome_off" };
  }
  if (observation.source.type === "windows") {
    return { ok: true, observation, deniedDomain: false };
  }
  if (observation.source.type !== "chrome") {
    return { ok: false, reason: "source" };
  }
  const domain = normalizeDomain(observation.resource?.domain ?? "") ?? undefined;
  if (!domain || !permitted(settings, domain)) {
    return { ok: false, reason: "domain_not_permitted" };
  }
  if (settings.pageContentEnabled) {
    return { ok: true, observation: domain === observation.resource?.domain ? observation : { ...observation, resource: { ...observation.resource, domain } }, deniedDomain: false };
  }
  const data = { ...(observation.data ?? {}) };
  delete data.text;
  delete data.heading;
  delete data.sections;
  return {
    ok: true,
    deniedDomain: false,
    observation: {
      ...observation,
      resource: { ...observation.resource, domain },
      data,
    },
  };
}
