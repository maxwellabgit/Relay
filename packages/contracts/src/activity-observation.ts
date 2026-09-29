/**
 * Computer-activity observation. Distinct from connector ObservationBinding
 * and from Reflex learning episodes stored in work_episodes.
 * Job application is a classifier over these records, not a field on Observation.
 */

export const ACTIVITY_RAW_RETENTION_DAYS = 7;
export const ACTIVITY_EPISODE_RETENTION_DAYS = 90;
export const ACTIVITY_TRACE_LIMIT = 500;
export const ACTIVITY_OBSERVATION_LIMIT = 2000;

export type ObservationSourceType = "windows" | "chrome" | "filesystem" | "relay";

export type ObservationSource = {
  readonly type: ObservationSourceType;
  readonly provider?: string;
  readonly deviceId?: string;
};

export type ObservationApplication = {
  readonly processName?: string;
  readonly executable?: string;
  readonly windowTitle?: string;
};

export type ObservationResource = {
  readonly uri?: string;
  readonly domain?: string;
  readonly title?: string;
  readonly path?: string;
};

export type Observation = {
  readonly id: string;
  readonly timestamp: string;
  readonly source: ObservationSource;
  readonly eventType: string;
  readonly application?: ObservationApplication;
  readonly resource?: ObservationResource;
  readonly data?: Readonly<Record<string, unknown>>;
  readonly sensitivity?: string;
  readonly permissionReceiptId?: string;
  readonly episodeId?: string;
  readonly caseId?: string;
};

export type ActivitySettings = {
  readonly enabled: boolean;
  readonly windowsEnabled: boolean;
  readonly chromeEnabled: boolean;
  readonly pageContentEnabled: boolean;
  readonly permittedDomains: readonly string[];
  readonly retentionDays: number;
};

export type EpisodeClassificationLabel =
  | "job_research"
  | "job_application"
  | "general_browsing"
  | "development" // pragma: allowlist secret
  | "unknown";

export type EpisodeClassification = {
  readonly label: EpisodeClassificationLabel;
  readonly confidence: number;
  readonly why: readonly string[];
};

export type EpisodeEvidence = {
  readonly observationId: string;
  readonly kind: string;
  readonly summary: string;
  readonly at: string;
};

export type EpisodeResource = {
  readonly label: string;
  readonly kind: "page" | "file" | "window";
  readonly uri?: string;
  readonly path?: string;
};

export type WorkEpisode = {
  readonly id: string;
  readonly origin: "live" | "replay";
  readonly status: "open" | "closed";
  readonly startedAt: string;
  readonly endedAt: string | null;
  readonly title: string;
  readonly thread: string;
  readonly applications: readonly string[];
  readonly resources: readonly EpisodeResource[];
  readonly evidence: readonly EpisodeEvidence[];
  readonly observationIds: readonly string[];
  /** Stable tokens for a later pattern-candidate stage. Not a Reflex. */
  readonly sequence: readonly string[];
  readonly classification: EpisodeClassification;
  readonly caseId: string | null;
  readonly caseConfidence: number | null;
  readonly caseEvidence: readonly string[];
  readonly caseCandidateLabel: string | null;
  readonly manualCase: boolean;
};

export type ActivityTraceType =
  | "observation.received"
  | "observation.rejected"
  | "observation.correlated"
  | "episode.started"
  | "episode.updated"
  | "episode.closed"
  | "episode.classified"
  | "episode.case_candidate"
  | "episode.case_assigned"
  | "permission.allowed"
  | "permission.denied"
  | "observer.started"
  | "observer.stopped"
  | "chrome.connected"
  | "chrome.disconnected";

export type ActivityTraceEvent = {
  readonly id: string;
  readonly at: string;
  readonly type: ActivityTraceType;
  readonly message: string;
  readonly observationId?: string;
  readonly episodeId?: string;
  readonly reason?: string;
  readonly confidence?: number;
};

export type CaseCorrection = {
  readonly episodeId: string;
  readonly caseId: string | null;
  readonly at: string;
};

export type CurrentActivityView = {
  readonly application: string;
  readonly title: string;
  readonly classification: string;
  readonly caseLabel: string | null;
  readonly observing: readonly string[];
  readonly notObserving: readonly string[];
  readonly episodeId: string | null;
};

export type ComputerActivityView = {
  readonly settings: ActivitySettings;
  readonly retentionLabel: string;
  readonly windowsObserver: "running" | "stopped" | "unavailable";
  readonly chromeConnection: "connected" | "disconnected" | "stopped";
  readonly current: CurrentActivityView | null;
  readonly episodes: readonly WorkEpisode[];
  readonly example: WorkEpisode | null;
  readonly trace: readonly ActivityTraceEvent[];
  readonly permittedDomains: readonly string[];
};

export const DEFAULT_ACTIVITY_SETTINGS: ActivitySettings = {
  enabled: false,
  windowsEnabled: true,
  chromeEnabled: true,
  pageContentEnabled: false,
  permittedDomains: [],
  retentionDays: ACTIVITY_RAW_RETENTION_DAYS,
};
