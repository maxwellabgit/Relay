import type { Observation, WorkEpisode } from "@relay/contracts";
import { CORRELATION_MS, EPISODE_BLIP_MS, EPISODE_GAP_MS, MAX_TITLE } from "./bounds.js";
import { associateEpisode, type CaseHint } from "./cases.js";
import { classifyEpisode } from "./classify.js";
import { pushTrace, type ActivityDocument } from "./document.js";
import { applicationLabel, fileFromTitle, jobRelated, readSignals, sequenceToken, threadFor } from "./signals.js";

type IdFactory = { next(prefix: string): string };

function titlesSimilar(left: string, right: string): boolean {
  const normalize = (value: string) =>
    value
      .toLowerCase()
      .replace(/\s+[-|]\s+(google chrome|chrome|microsoft word|word|cursor)$/i, "")
      .replace(/[^a-z0-9]+/g, " ")
      .trim();
  const a = normalize(left);
  const b = normalize(right);
  if (!a || !b) return false;
  if (a.includes(b) || b.includes(a)) return true;
  const leftWords = new Set(a.split(" ").filter((word) => word.length > 2));
  const rightWords = b.split(" ").filter((word) => word.length > 2);
  if (leftWords.size === 0 || rightWords.length === 0) return false;
  const overlap = rightWords.filter((word) => leftWords.has(word)).length;
  return overlap / Math.max(leftWords.size, rightWords.length) >= 0.6;
}

function browserProcess(observation: Observation): boolean {
  const name = (observation.application?.processName ?? "").toLowerCase();
  return name === "chrome.exe" || name === "msedge.exe" || name === "brave.exe" || name === "firefox.exe";
}

export function enrichObservation(observation: Observation): Observation {
  const title = observation.application?.windowTitle ?? "";
  const file = observation.resource?.path ?? fileFromTitle(title);
  if (!file || observation.resource?.path) return observation;
  return {
    ...observation,
    resource: {
      ...observation.resource,
      path: file,
      title: observation.resource?.title ?? file,
    },
  };
}

function evidenceFor(observation: Observation): WorkEpisode["evidence"][number][] {
  const signals = readSignals(observation);
  const at = observation.timestamp;
  const title = observation.resource?.title ?? observation.application?.windowTitle ?? observation.resource?.path ?? "Window";
  const summary = title.slice(0, 180);
  const rows: WorkEpisode["evidence"][number][] = [];
  if (signals.jobPosting) rows.push({ observationId: observation.id, kind: "job_posting", summary, at });
  if (signals.jobDescription) {
    const text = typeof observation.data?.text === "string" ? observation.data.text.slice(0, 180) : summary;
    rows.push({ observationId: observation.id, kind: "job_description", summary: text, at });
  }
  if (signals.resume) rows.push({ observationId: observation.id, kind: "resume", summary, at });
  if (signals.applicationPage) rows.push({ observationId: observation.id, kind: "application_page", summary, at });
  if (signals.development) rows.push({ observationId: observation.id, kind: "development", summary, at }); // pragma: allowlist secret
  if (rows.length === 0) {
    const kind = observation.resource?.path ? "file" : observation.resource?.uri ? "page" : "window";
    rows.push({ observationId: observation.id, kind, summary, at });
  }
  return rows;
}

function resourceFor(observation: Observation): WorkEpisode["resources"][number] | null {
  const label = (observation.resource?.title ?? observation.resource?.path ?? observation.application?.windowTitle ?? "").slice(0, MAX_TITLE);
  if (!label) return null;
  if (observation.resource?.path) {
    return { label: observation.resource.path, kind: "file", path: observation.resource.path };
  }
  if (observation.resource?.uri) {
    return {
      label,
      kind: "page",
      uri: observation.resource.uri,
    };
  }
  return { label, kind: "window" };
}

function withAssociation(episode: WorkEpisode, cases: readonly CaseHint[]): WorkEpisode {
  const association = associateEpisode(episode, cases);
  return { ...episode, ...association };
}

function retitle(episode: WorkEpisode): string {
  const role = episode.resources.find((item) =>
    /\b(engineer|designer|manager|scientist|analyst|developer|director)\b/i.test(item.label),
  );
  return role?.label ?? episode.resources[0]?.label ?? episode.applications[0] ?? "Activity";
}

function reclassify(episode: WorkEpisode, cases: readonly CaseHint[]): WorkEpisode {
  const classification = classifyEpisode(episode);
  const next = withAssociation({ ...episode, classification, title: retitle({ ...episode, classification }) }, cases);
  return next;
}

function attach(episode: WorkEpisode, observation: Observation, cases: readonly CaseHint[]): WorkEpisode {
  const signals = readSignals(observation);
  const app = applicationLabel(observation.application?.processName);
  const applications = [...episode.applications.filter((item) => item !== app), app];
  const resource = resourceFor(observation);
  const resources = resource
    ? episode.resources.some((item) => item.label === resource.label)
      ? episode.resources
      : [...episode.resources, resource]
    : episode.resources;
  const evidence = [...episode.evidence, ...evidenceFor(observation)];
  const observationIds = episode.observationIds.includes(observation.id)
    ? episode.observationIds
    : [...episode.observationIds, observation.id];
  const sequence = [...episode.sequence, sequenceToken(observation)].slice(-40);
  const thread = episode.thread === "job" || jobRelated(signals) ? "job" : threadFor(observation, signals);
  return reclassify(
    {
      ...episode,
      applications,
      resources,
      evidence,
      observationIds,
      sequence,
      thread: episode.thread === "job" ? "job" : thread,
      endedAt: observation.timestamp,
    },
    cases,
  );
}

function continues(open: WorkEpisode, observation: Observation): boolean {
  const previous = Date.parse(open.endedAt ?? open.startedAt);
  const at = Date.parse(observation.timestamp);
  const gap = at - previous;
  if (!Number.isFinite(gap) || gap >= EPISODE_GAP_MS || gap < -CORRELATION_MS) return false;
  const signals = readSignals(observation);
  if (open.thread === "job" && jobRelated(signals)) return true;
  if (jobRelated(signals) && open.evidence.some((item) => item.kind === "resume" || item.kind === "job_posting")) {
    return true;
  }
  return threadFor(observation, signals) === open.thread;
}

function findCorrelatedWindows(doc: ActivityDocument, observation: Observation): Observation | null {
  if (observation.source.type !== "chrome") return null;
  const title = observation.resource?.title ?? observation.application?.windowTitle ?? "";
  const at = Date.parse(observation.timestamp);
  for (let index = doc.observations.length - 1; index >= 0 && index >= doc.observations.length - 30; index -= 1) {
    const prior = doc.observations[index];
    if (!prior || prior.data?.supersededBy) continue;
    if (Math.abs(at - Date.parse(prior.timestamp)) > CORRELATION_MS) continue;
    if (prior.source.type !== "windows" || !browserProcess(prior)) continue;
    const priorTitle = prior.application?.windowTitle ?? "";
    if (titlesSimilar(title, priorTitle)) return prior;
  }
  return null;
}

function findCorrelatedChrome(doc: ActivityDocument, observation: Observation): Observation | null {
  if (observation.source.type !== "windows" || !browserProcess(observation)) return null;
  const title = observation.application?.windowTitle ?? "";
  const at = Date.parse(observation.timestamp);
  for (let index = doc.observations.length - 1; index >= 0 && index >= doc.observations.length - 30; index -= 1) {
    const prior = doc.observations[index];
    if (!prior || prior.data?.supersededBy) continue;
    if (Math.abs(at - Date.parse(prior.timestamp)) > CORRELATION_MS) continue;
    if (prior.source.type !== "chrome") continue;
    const priorTitle = prior.resource?.title ?? prior.application?.windowTitle ?? "";
    if (titlesSimilar(title, priorTitle)) return prior;
  }
  return null;
}

function replaceEpisode(doc: ActivityDocument, episode: WorkEpisode): void {
  const index = doc.episodes.findIndex((item) => item.id === episode.id);
  if (index >= 0) doc.episodes[index] = episode;
}

function traceClassification(doc: ActivityDocument, ids: IdFactory, at: string, before: WorkEpisode | null, episode: WorkEpisode): void {
  if (!before || before.classification.label !== episode.classification.label) {
    pushTrace(doc, ids.next("trace"), at, "episode.classified", episode.classification.why.join(" "), {
      episodeId: episode.id,
      confidence: episode.classification.confidence,
    });
  }
  if (!episode.manualCase && episode.caseCandidateLabel && episode.caseCandidateLabel !== before?.caseCandidateLabel) {
    pushTrace(doc, ids.next("trace"), at, "episode.case_candidate", episode.caseCandidateLabel, {
      episodeId: episode.id,
      ...(episode.caseConfidence != null ? { confidence: episode.caseConfidence } : {}),
    });
  }
  if (episode.caseId && episode.caseId !== before?.caseId) {
    pushTrace(doc, ids.next("trace"), at, "episode.case_assigned", episode.caseCandidateLabel ?? episode.caseId, {
      episodeId: episode.id,
    });
  }
}

export function ingestReduced(
  doc: ActivityDocument,
  raw: Observation,
  cases: readonly CaseHint[],
  ids: IdFactory,
  at: string,
): { readonly episodeId: string | null; readonly correlated: boolean } {
  const observation = enrichObservation(raw);
  const chromeMatch = findCorrelatedChrome(doc, observation);
  if (chromeMatch) {
    doc.observations.push({
      ...observation,
      data: { ...(observation.data ?? {}), supersededBy: chromeMatch.id },
      ...(chromeMatch.episodeId ? { episodeId: chromeMatch.episodeId } : {}),
    });
    pushTrace(doc, ids.next("trace"), at, "observation.correlated", "Windows Chrome focus matches extension context.", {
      observationId: observation.id,
      ...(chromeMatch.episodeId ? { episodeId: chromeMatch.episodeId } : {}),
    });
    return { episodeId: chromeMatch.episodeId ?? null, correlated: true };
  }

  const windowsMatch = findCorrelatedWindows(doc, observation);
  doc.observations.push(observation);
  if (windowsMatch) {
    const superseded: Observation = {
      ...windowsMatch,
      data: { ...(windowsMatch.data ?? {}), supersededBy: observation.id },
    };
    const priorIndex = doc.observations.findIndex((item) => item.id === windowsMatch.id);
    if (priorIndex >= 0) doc.observations[priorIndex] = superseded;
    const host = doc.episodes.find((item) => item.observationIds.includes(windowsMatch.id));
    if (host) {
      const before = host;
      const title = observation.resource?.title ?? observation.application?.windowTitle ?? "";
      const next = attach(
        {
          ...host,
          observationIds: host.observationIds.filter((id) => id !== windowsMatch.id),
          resources: host.resources.filter((item) => !titlesSimilar(item.label, title)),
          evidence: host.evidence.filter((item) => item.observationId !== windowsMatch.id),
        },
        observation,
        cases,
      );
      const stored = { ...next, observationIds: [...next.observationIds] };
      replaceEpisode(doc, stored);
      const linked = { ...observation, episodeId: stored.id };
      const self = doc.observations.findIndex((item) => item.id === observation.id);
      if (self >= 0) doc.observations[self] = linked;
      pushTrace(doc, ids.next("trace"), at, "observation.correlated", "Chrome page context replaced the window-title-only Chrome focus.", {
        observationId: observation.id,
        episodeId: stored.id,
      });
      pushTrace(doc, ids.next("trace"), at, "episode.updated", stored.title, { episodeId: stored.id });
      traceClassification(doc, ids, at, before, stored);
      return { episodeId: stored.id, correlated: true };
    }
  }

  pushTrace(doc, ids.next("trace"), at, "observation.received", observation.eventType, { observationId: observation.id });
  const open = doc.episodes.find((item) => item.status === "open" && item.origin === "live");
  if (open && continues(open, observation)) {
    const before = open;
    const next = attach(open, observation, cases);
    replaceEpisode(doc, next);
    const self = doc.observations.findIndex((item) => item.id === observation.id);
    if (self >= 0) doc.observations[self] = { ...observation, episodeId: next.id };
    pushTrace(doc, ids.next("trace"), at, "episode.updated", next.title, { episodeId: next.id });
    traceClassification(doc, ids, at, before, next);
    return { episodeId: next.id, correlated: false };
  }
  const resumed = open && !continues(open, observation) ? resumeAfterBlip(doc, open, observation) : null;
  if (resumed) {
    doc.episodes = doc.episodes.filter((item) => item.id !== open?.id);
    const next = attach({ ...resumed, status: "open" }, observation, cases);
    replaceEpisode(doc, next);
    const self = doc.observations.findIndex((item) => item.id === observation.id);
    if (self >= 0) doc.observations[self] = { ...observation, episodeId: next.id };
    pushTrace(doc, ids.next("trace"), at, "episode.updated", next.title, { episodeId: next.id });
    traceClassification(doc, ids, at, resumed, next);
    return { episodeId: next.id, correlated: false };
  }
  if (open) {
    const closed = { ...open, status: "closed" as const };
    replaceEpisode(doc, closed);
    pushTrace(doc, ids.next("trace"), at, "episode.closed", closed.title, { episodeId: closed.id });
  }
  const signals = readSignals(observation);
  const created = reclassify(
    {
      id: ids.next("episode"),
      origin: "live",
      status: "open",
      startedAt: observation.timestamp,
      endedAt: observation.timestamp,
      title: "Activity",
      thread: threadFor(observation, signals),
      applications: [],
      resources: [],
      evidence: [],
      observationIds: [],
      sequence: [],
      classification: { label: "unknown", confidence: 0, why: [] },
      caseId: null,
      caseConfidence: null,
      caseEvidence: [],
      caseCandidateLabel: null,
      manualCase: false,
    },
    cases,
  );
  const next = attach(created, observation, cases);
  const opened = { ...next, status: "open" as const };
  doc.episodes.push(opened);
  const self = doc.observations.findIndex((item) => item.id === observation.id);
  if (self >= 0) doc.observations[self] = { ...observation, episodeId: opened.id };
  pushTrace(doc, ids.next("trace"), at, "episode.started", opened.title, { episodeId: opened.id });
  traceClassification(doc, ids, at, null, opened);
  return { episodeId: opened.id, correlated: Boolean(windowsMatch) };
}

function resumeAfterBlip(doc: ActivityDocument, open: WorkEpisode, observation: Observation): WorkEpisode | null {
  const at = Date.parse(observation.timestamp);
  const openStart = Date.parse(open.startedAt);
  if (!Number.isFinite(at) || !Number.isFinite(openStart) || at - openStart >= EPISODE_BLIP_MS) return null;
  const prior = [...doc.episodes].reverse().find((item) => item.origin === "live" && item.id !== open.id && item.status === "closed");
  if (!prior) return null;
  const gap = at - Date.parse(prior.endedAt ?? prior.startedAt);
  if (!Number.isFinite(gap) || gap < 0 || gap >= EPISODE_GAP_MS) return null;
  if (!continues(prior, observation)) return null;
  return prior;
}

export function closeStaleEpisodes(doc: ActivityDocument, nowMs: number, ids: IdFactory, at: string): void {
  for (const episode of doc.episodes) {
    if (episode.status !== "open" || episode.origin !== "live") continue;
    const end = Date.parse(episode.endedAt ?? episode.startedAt);
    if (!Number.isFinite(end) || nowMs - end < EPISODE_GAP_MS) continue;
    replaceEpisode(doc, { ...episode, status: "closed" });
    pushTrace(doc, ids.next("trace"), at, "episode.closed", episode.title, { episodeId: episode.id });
  }
}

export function closeOpenEpisodes(doc: ActivityDocument, ids: IdFactory, at: string): void {
  for (const episode of doc.episodes) {
    if (episode.status !== "open" || episode.origin !== "live") continue;
    const closed = { ...episode, status: "closed" as const };
    replaceEpisode(doc, closed);
    pushTrace(doc, ids.next("trace"), at, "episode.closed", closed.title, { episodeId: closed.id });
  }
}
