import {
  ACTIVITY_EPISODE_RETENTION_DAYS,
  DEFAULT_ACTIVITY_SETTINGS,
} from "@relay/contracts";
import type {
  ActivitySettings,
  ActivitySettingsPatch,
  ComputerActivityView,
  CurrentActivityView,
  Observation,
  RelayCommandResult,
  WorkEpisode,
} from "@relay/contracts";
import type { FoundationStore } from "../cases/foundation-store.js";
import type { Clock, IdFactory } from "../scheduler.js";
import { classificationDisplay } from "./classify.js";
import { type CaseHint } from "./cases.js";
import { normalizeDomain } from "./domains.js";
import { emptyDocument, isDocument, pruneDocument, pushTrace, type ActivityDocument, type ObserverChrome, type ObserverWindows } from "./document.js";
import { JOB_APPLICATION_SETTINGS, jobApplicationObservations } from "./fixture.js";
import { validateNativeMessage } from "./native-message.js";
import { applyObservationPolicy } from "./policy.js";
import { closeOpenEpisodes, ingestReduced } from "./reduce.js";
import { isDuplicate, validateObservation } from "./validate.js";

const KIND = "activity.observation";
const RECORD_ID = "current";

export type ActivityServiceDeps = {
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly records: FoundationStore;
  readonly listCases: () => Promise<readonly CaseHint[]>;
};

export class ActivityObservationService {
  private doc: ActivityDocument = emptyDocument();
  private version = 0;
  private loaded = false;
  private chain: Promise<void> = Promise.resolve();
  private hintCache: CaseHint[] = [];
  private hintCacheAt = 0;

  constructor(private readonly deps: ActivityServiceDeps) {}

  private run<T>(work: () => Promise<T>): Promise<T> {
    const pending = this.chain.then(work, work);
    this.chain = pending.then(
      () => undefined,
      () => undefined,
    );
    return pending;
  }

  load(): Promise<void> {
    return this.run(() => this.loadInner());
  }

  async shutdown(): Promise<void> {
    await this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const at = this.deps.clock.now().toISOString();
      closeOpenEpisodes(this.doc, this.deps.ids, at);
      if (this.doc.windowsObserver === "running") {
        this.doc.windowsObserver = "stopped";
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "observer.stopped", "Windows observer stopped.");
      }
      if (this.doc.chromeConnection === "connected") {
        this.doc.chromeConnection = "disconnected";
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "chrome.disconnected", "Chrome bridge disconnected.");
      }
      await this.save();
    });
  }

  view(): ComputerActivityView {
    return project(this.doc);
  }

  settings(): ActivitySettings {
    return this.doc.settings;
  }

  ingest(input: unknown): Promise<RelayCommandResult & { readonly toolsInvoked: readonly [] }> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const at = this.deps.clock.now().toISOString();
      const granted = permissionGrant(input);
      if (granted) {
        if (!this.doc.settings.enabled || !this.doc.settings.chromeEnabled) {
          pushTrace(this.doc, this.deps.ids.next("trace"), at, "permission.denied", granted, {
            reason: "observation_off",
          });
          await this.save();
          return { ok: false, summary: "observation_off", error: "observation_off", toolsInvoked: [] as const };
        }
        if (!this.doc.settings.permittedDomains.includes(granted)) {
          this.doc.settings = {
            ...this.doc.settings,
            permittedDomains: [...this.doc.settings.permittedDomains, granted],
          };
        }
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "permission.allowed", granted);
        await this.save();
        return { ok: true, summary: "domain_permitted", toolsInvoked: [] as const };
      }
      const validated = validateObservation(input, this.deps.ids.next("obs"));
      if (!validated.ok) {
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "observation.rejected", validated.reason, {
          reason: validated.reason,
        });
        await this.save();
        return { ok: false, summary: validated.reason, error: validated.reason, toolsInvoked: [] as const };
      }
      const policy = applyObservationPolicy(validated.observation, this.doc.settings);
      if (!policy.ok) {
        const denied = policy.reason === "domain_not_permitted";
        pushTrace(this.doc, this.deps.ids.next("trace"), at, denied ? "permission.denied" : "observation.rejected", policy.reason, {
          reason: policy.reason,
        });
        await this.save();
        return { ok: false, summary: policy.reason, error: policy.reason, toolsInvoked: [] as const };
      }
      if (isDuplicate(this.doc.observations, policy.observation)) {
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "observation.rejected", "duplicate", { reason: "duplicate" });
        await this.save();
        return { ok: false, summary: "duplicate", error: "duplicate", toolsInvoked: [] as const };
      }
      const cases = await this.cachedCases();
      const reduced = ingestReduced(this.doc, policy.observation, cases, this.deps.ids, at);
      this.doc = pruneDocument(this.doc, this.deps.clock.now());
      await this.save();
      return {
        ok: true,
        summary: reduced.correlated ? "observation_correlated" : "observation_received",
        toolsInvoked: [] as const,
      };
    });
  }

  acceptNativeMessage(input: unknown, byteLength: number): Promise<RelayCommandResult & { readonly toolsInvoked: readonly [] }> {
    const decision = validateNativeMessage(input, byteLength);
    if (!decision.ok) {
      return this.run(async () => {
        if (!this.loaded) await this.loadInner();
        const at = this.deps.clock.now().toISOString();
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "observation.rejected", decision.reason, {
          reason: decision.reason,
        });
        await this.save();
        return { ok: false, summary: decision.reason, error: decision.reason, toolsInvoked: [] as const };
      });
    }
    if (decision.kind === "hello") {
      return this.setObserverStatus({ chrome: "connected" });
    }
    return this.ingest(decision.observation);
  }

  applySettings(patch: ActivitySettingsPatch): Promise<RelayCommandResult> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const at = this.deps.clock.now().toISOString();
      const next: ActivitySettings = {
        ...this.doc.settings,
        ...(patch.enabled !== undefined ? { enabled: patch.enabled } : {}),
        ...(patch.windowsEnabled !== undefined ? { windowsEnabled: patch.windowsEnabled } : {}),
        ...(patch.chromeEnabled !== undefined ? { chromeEnabled: patch.chromeEnabled } : {}),
        ...(patch.pageContentEnabled !== undefined ? { pageContentEnabled: patch.pageContentEnabled } : {}),
        ...(patch.retentionDays !== undefined
          ? { retentionDays: Math.min(30, Math.max(1, Math.round(patch.retentionDays))) }
          : {}),
      };
      if (next.enabled !== this.doc.settings.enabled) {
        pushTrace(
          this.doc,
          this.deps.ids.next("trace"),
          at,
          next.enabled ? "observer.started" : "observer.stopped",
          next.enabled ? "Observation enabled." : "Observation paused.",
        );
      }
      this.doc.settings = next;
      this.doc = pruneDocument(this.doc, this.deps.clock.now());
      await this.save();
      return { ok: true, summary: next.enabled ? "observation_on" : "observation_off" };
    });
  }

  permitDomain(domain: string): Promise<RelayCommandResult> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const normalized = normalizeDomain(domain);
      const at = this.deps.clock.now().toISOString();
      if (!normalized) {
        pushTrace(this.doc, this.deps.ids.next("trace"), at, "permission.denied", "Domain was not allowed.", {
          reason: "invalid_domain",
        });
        await this.save();
        return { ok: false, summary: "invalid_domain", error: "invalid_domain" };
      }
      if (!this.doc.settings.permittedDomains.includes(normalized)) {
        this.doc.settings = {
          ...this.doc.settings,
          permittedDomains: [...this.doc.settings.permittedDomains, normalized],
        };
      }
      pushTrace(this.doc, this.deps.ids.next("trace"), at, "permission.allowed", normalized);
      await this.save();
      return { ok: true, summary: "domain_permitted" };
    });
  }

  revokeDomain(domain: string): Promise<RelayCommandResult> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const normalized = normalizeDomain(domain);
      if (!normalized) return { ok: false, summary: "invalid_domain", error: "invalid_domain" };
      this.doc.settings = {
        ...this.doc.settings,
        permittedDomains: this.doc.settings.permittedDomains.filter((item) => item !== normalized),
      };
      pushTrace(this.doc, this.deps.ids.next("trace"), this.deps.clock.now().toISOString(), "permission.denied", normalized, {
        reason: "revoked",
      });
      await this.save();
      return { ok: true, summary: "domain_revoked" };
    });
  }

  clearHistory(): Promise<RelayCommandResult> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const settings = this.doc.settings;
      const windowsObserver = this.doc.windowsObserver;
      const chromeConnection = this.doc.chromeConnection;
      this.doc = { ...emptyDocument(), settings, windowsObserver, chromeConnection };
      await this.save();
      return { ok: true, summary: "activity_cleared" };
    });
  }

  assign(episodeId: string, caseId: string | null, label: string | null): Promise<RelayCommandResult> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const episode = this.doc.episodes.find((item) => item.id === episodeId) ?? (this.doc.example?.id === episodeId ? this.doc.example : null);
      if (!episode) return { ok: false, summary: "episode_missing", error: "episode_missing" };
      const at = this.deps.clock.now().toISOString();
      const next: WorkEpisode = {
        ...episode,
        caseId,
        caseCandidateLabel: label ?? episode.caseCandidateLabel,
        caseConfidence: caseId ? 1 : null,
        caseEvidence: caseId ? ["Manual assignment."] : ["Manual clear."],
        manualCase: true,
      };
      if (this.doc.example?.id === episodeId) this.doc.example = next;
      else {
        const index = this.doc.episodes.findIndex((item) => item.id === episodeId);
        if (index >= 0) this.doc.episodes[index] = next;
      }
      this.doc.corrections.push({ episodeId, caseId, at });
      pushTrace(this.doc, this.deps.ids.next("trace"), at, "episode.case_assigned", label ?? caseId ?? "cleared", {
        episodeId,
      });
      await this.save();
      return { ok: true, summary: "episode_assigned", ...(caseId ? { caseId } : {}) };
    });
  }

  setObserverStatus(status: { windows?: ObserverWindows; chrome?: ObserverChrome }): Promise<RelayCommandResult & { readonly toolsInvoked: readonly [] }> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const at = this.deps.clock.now().toISOString();
      if (status.windows && status.windows !== this.doc.windowsObserver) {
        this.doc.windowsObserver = status.windows;
        pushTrace(
          this.doc,
          this.deps.ids.next("trace"),
          at,
          status.windows === "running" ? "observer.started" : "observer.stopped",
          `Windows observer ${status.windows}.`,
        );
      }
      if (status.chrome && status.chrome !== this.doc.chromeConnection) {
        this.doc.chromeConnection = status.chrome;
        pushTrace(
          this.doc,
          this.deps.ids.next("trace"),
          at,
          status.chrome === "connected" ? "chrome.connected" : "chrome.disconnected",
          `Chrome ${status.chrome}.`,
        );
      }
      await this.save();
      return { ok: true, summary: "observer_status", toolsInvoked: [] as const };
    });
  }

  replayJobApplication(cases: readonly CaseHint[] = []): Promise<RelayCommandResult & { readonly toolsInvoked: readonly [] }> {
    return this.run(async () => {
      if (!this.loaded) await this.loadInner();
      const example = reconstructJobEpisode(cases, this.deps.ids);
      this.doc.example = example;
      await this.save();
      return {
        ok: Boolean(example),
        summary: example?.classification.label ?? "replay_empty",
        toolsInvoked: [] as const,
      };
    });
  }

  private async cachedCases(): Promise<readonly CaseHint[]> {
    const now = this.deps.clock.now().getTime();
    if (this.hintCacheAt !== 0 && now - this.hintCacheAt < 30_000) return this.hintCache;
    this.hintCache = [...(await this.deps.listCases())];
    this.hintCacheAt = now;
    return this.hintCache;
  }

  private async loadInner(): Promise<void> {
    const row = await this.deps.records.get(KIND, RECORD_ID);
    if (row && isDocument(row.payload)) {
      const defaults = emptyDocument();
      this.doc = pruneDocument(
        {
          ...defaults,
          ...row.payload,
          settings: {
            ...defaults.settings,
            ...(row.payload.settings ?? {}),
            permittedDomains: row.payload.settings?.permittedDomains ?? [],
          },
        },
        this.deps.clock.now(),
      );
      this.version = row.version;
    }
    this.loaded = true;
  }

  private async save(): Promise<void> {
    this.version += 1;
    await this.deps.records.put(KIND, RECORD_ID, this.version, this.doc, this.deps.clock.now().toISOString());
  }
}

export function reconstructJobEpisode(cases: readonly CaseHint[], ids: IdFactory): WorkEpisode | null {
  const doc = emptyDocument();
  doc.settings = { ...JOB_APPLICATION_SETTINGS, permittedDomains: [...JOB_APPLICATION_SETTINGS.permittedDomains] };
  let sequence = 0;
  const replayIds: IdFactory = { next: (prefix) => `${prefix}_replay_${++sequence}` };
  for (const input of jobApplicationObservations()) {
    const validated = validateObservation(input, replayIds.next("obs"));
    if (!validated.ok) continue;
    const policy = applyObservationPolicy(validated.observation, doc.settings);
    if (!policy.ok || policy.deniedDomain) continue;
    ingestReduced(doc, policy.observation, cases, replayIds, validated.observation.timestamp);
  }
  closeOpenEpisodes(doc, replayIds, "2026-09-29T10:47:00.000Z");
  const episode = [...doc.episodes].reverse().find((item) => item.classification.label === "job_application") ?? doc.episodes.at(-1) ?? null;
  if (!episode) return null;
  return { ...episode, origin: "replay", status: "closed", id: ids.next("episode") };
}

function permissionGrant(input: unknown): string | null {
  if (!input || typeof input !== "object") return null;
  const row = input as Record<string, unknown>;
  if (row.eventType !== "chrome.permission.changed") return null;
  const data = row.data;
  if (!data || typeof data !== "object" || Array.isArray(data)) return null;
  if ((data as Record<string, unknown>).permitted !== true) return null;
  const resource = row.resource;
  if (!resource || typeof resource !== "object" || Array.isArray(resource)) return null;
  const domain = (resource as Record<string, unknown>).domain;
  return typeof domain === "string" ? normalizeDomain(domain) : null;
}

function project(doc: ActivityDocument): ComputerActivityView {
  const open = [...doc.episodes].reverse().find((item) => item.status === "open" && item.origin === "live") ?? null;
  const episodes = [...doc.episodes].filter((item) => item.origin === "live").sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));
  return {
    settings: doc.settings,
    retentionLabel: `Raw observations ${doc.settings.retentionDays || DEFAULT_ACTIVITY_SETTINGS.retentionDays} days · episodes ${ACTIVITY_EPISODE_RETENTION_DAYS} days`,
    windowsObserver: doc.windowsObserver,
    chromeConnection: doc.chromeConnection,
    current: open ? currentFrom(open, doc.settings) : null,
    episodes,
    example: doc.example,
    trace: [...doc.trace].reverse(),
    permittedDomains: doc.settings.permittedDomains,
  };
}

function currentFrom(episode: WorkEpisode, settings: ActivitySettings): CurrentActivityView {
  const observing = [
    ...(settings.windowsEnabled ? ["application", "window title"] : []),
    ...(settings.chromeEnabled && settings.pageContentEnabled ? ["permitted page context"] : []),
    ...(settings.chromeEnabled && !settings.pageContentEnabled ? ["permitted tab metadata"] : []),
  ];
  return {
    application: episode.applications.at(-1) ?? "Unknown",
    title: episode.title,
    classification: classificationDisplay(episode.classification.label),
    caseLabel: episode.caseCandidateLabel,
    observing,
    notObserving: ["keystrokes", "screen recording", "passwords"],
    episodeId: episode.id,
  };
}

export type { Observation };
