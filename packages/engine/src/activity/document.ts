import {
  ACTIVITY_EPISODE_RETENTION_DAYS,
  ACTIVITY_OBSERVATION_LIMIT,
  ACTIVITY_RAW_RETENTION_DAYS,
  ACTIVITY_TRACE_LIMIT,
  DEFAULT_ACTIVITY_SETTINGS,
} from "@relay/contracts";
import type {
  ActivitySettings,
  ActivityTraceEvent,
  ActivityTraceType,
  CaseCorrection,
  Observation,
  WorkEpisode,
} from "@relay/contracts";
import { plainObject } from "./bounds.js";

export type ObserverWindows = "running" | "stopped" | "unavailable";
export type ObserverChrome = "connected" | "disconnected" | "stopped";

export type ActivityDocument = {
  version: 1;
  settings: ActivitySettings;
  observations: Observation[];
  episodes: WorkEpisode[];
  example: WorkEpisode | null;
  trace: ActivityTraceEvent[];
  corrections: CaseCorrection[];
  windowsObserver: ObserverWindows;
  chromeConnection: ObserverChrome;
};

export function emptyDocument(): ActivityDocument {
  return {
    version: 1,
    settings: { ...DEFAULT_ACTIVITY_SETTINGS, permittedDomains: [] },
    observations: [],
    episodes: [],
    example: null,
    trace: [],
    corrections: [],
    windowsObserver: "stopped",
    chromeConnection: "stopped",
  };
}

export function isDocument(value: unknown): value is ActivityDocument {
  const row = plainObject(value);
  if (!row || row.version !== 1) return false;
  return Array.isArray(row.observations) && Array.isArray(row.episodes) && Array.isArray(row.trace);
}

export function pushTrace(
  doc: ActivityDocument,
  id: string,
  at: string,
  type: ActivityTraceType,
  message: string,
  extra?: { observationId?: string; episodeId?: string; reason?: string; confidence?: number },
): void {
  const event: ActivityTraceEvent = {
    id,
    at,
    type,
    message,
    ...(extra?.observationId ? { observationId: extra.observationId } : {}),
    ...(extra?.episodeId ? { episodeId: extra.episodeId } : {}),
    ...(extra?.reason ? { reason: extra.reason } : {}),
    ...(extra?.confidence != null ? { confidence: extra.confidence } : {}),
  };
  doc.trace.push(event);
  if (doc.trace.length > ACTIVITY_TRACE_LIMIT) {
    doc.trace.splice(0, doc.trace.length - ACTIVITY_TRACE_LIMIT);
  }
}

export function pruneDocument(doc: ActivityDocument, now: Date): ActivityDocument {
  const rawMs = Math.max(1, doc.settings.retentionDays || ACTIVITY_RAW_RETENTION_DAYS) * 24 * 60 * 60 * 1000;
  const episodeMs = ACTIVITY_EPISODE_RETENTION_DAYS * 24 * 60 * 60 * 1000;
  const cutoff = now.getTime() - rawMs;
  const episodeCutoff = now.getTime() - episodeMs;
  const observations = doc.observations.filter((item) => Date.parse(item.timestamp) >= cutoff).slice(-ACTIVITY_OBSERVATION_LIMIT);
  const kept = new Set(observations.map((item) => item.id));
  const episodes = doc.episodes
    .filter((item) => item.status === "open" || Date.parse(item.endedAt ?? item.startedAt) >= episodeCutoff)
    .map((item) => ({
      ...item,
      observationIds: item.observationIds.filter((id) => kept.has(id)),
    }));
  const trace = doc.trace.filter((item) => Date.parse(item.at) >= cutoff).slice(-ACTIVITY_TRACE_LIMIT);
  return { ...doc, observations, episodes, trace };
}
