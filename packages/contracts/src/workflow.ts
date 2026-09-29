/** Local activity observation. Raw signals are not Case memory. */

export type WorkflowSourceType = "browser" | "desktop" | "relay";

export type WorkflowPermission = "always" | "session" | "metadata";

export type WorkflowEventType = "window.focus" | "browser.navigate" | "browser.activate" | "page.semantic";

/** Untrusted adapter payload. The runtime validates this before storage. */
export type RawActivitySignal = {
  readonly observedAt: string;
  readonly sourceType: WorkflowSourceType;
  readonly provider: string;
  readonly deviceId?: string;
  readonly eventType: string;
  readonly url?: string;
  readonly title?: string;
  readonly application?: string;
  readonly excerpt?: string;
  readonly permission?: WorkflowPermission;
  readonly durationMs?: number;
};

export type StoredObservation = {
  readonly observationId: string;
  readonly observedAt: string;
  readonly sourceType: WorkflowSourceType;
  readonly provider: string;
  readonly deviceId: string;
  readonly eventType: WorkflowEventType;
  readonly url: string | null;
  readonly title: string | null;
  readonly application: string | null;
  readonly excerpt: string | null;
  readonly excerptRef?: { readonly artifactId: string; readonly sha256: string } | null;
  readonly permission: WorkflowPermission;
  readonly permissionReceiptId: string;
  readonly durationMs: number | null;
  readonly sensitivity: "ordinary" | "personal" | "credential_surface";
  readonly episodeId: string | null;
  readonly retentionUntil: string;
};

export type EpisodeClassification = "job_application" | "job_research" | "uncertain" | "other";

export type EpisodeOutcomeKind = "research" | "in_progress" | "submitted" | "unknown";

export type EpisodeEvidence = {
  readonly observationId: string;
  readonly label: string;
  readonly application: string | null;
  readonly host: string | null;
  readonly at: string;
};

export type WorkEpisodeRecord = {
  readonly episodeId: string;
  readonly version: number;
  readonly classification: EpisodeClassification;
  readonly outcome: EpisodeOutcomeKind;
  readonly confidence: "high" | "medium" | "low";
  readonly rationale: string;
  readonly startedAt: string;
  readonly endedAt: string;
  readonly caseId: string | null;
  readonly company: string | null;
  readonly position: string | null;
  readonly evidence: readonly EpisodeEvidence[];
  readonly locked: boolean;
  readonly closed: boolean;
};

export type WorkflowSettings = {
  readonly setupComplete: boolean;
  readonly paused: boolean;
  readonly windowsEnabled: boolean;
  readonly chromeEnabled: boolean;
  readonly pageContentEnabled: boolean;
  readonly retentionDays: number;
  readonly allowedSites: readonly string[];
  readonly allowedFolders: readonly string[];
  readonly masterResumePath: string | null;
  readonly patternSuppressed: boolean;
};

export type WorkflowHostStatus = {
  readonly windows: "connected" | "paused" | "stopped" | "failed";
  readonly chrome: "connected" | "disconnected" | "paused";
  readonly windowsDetail: string;
  readonly chromeDetail: string;
};

export type WorkflowProposal = {
  readonly proposalId: string;
  readonly kind: "pattern" | "improvement";
  readonly state: "pending" | "accepted" | "rejected" | "suppressed";
  readonly title: string;
  readonly explanation: string;
  readonly episodeIds: readonly string[];
  readonly examples: readonly string[];
  readonly updatedAt: string;
};

export type WorkflowReflexState = "shadow" | "active" | "paused";

export type WorkflowReflexRecord = {
  readonly reflexId: "reflex.job-application";
  readonly version: number;
  readonly state: WorkflowReflexState;
  readonly trigger: string;
  readonly capabilities: readonly string[];
  readonly forbidden: readonly string[];
  readonly procedure: readonly string[];
  readonly preferences: readonly string[];
  readonly approval: "always_ask";
  readonly history: readonly {
    readonly version: number;
    readonly at: string;
    readonly note: string;
  }[];
  readonly runs: readonly {
    readonly receiptId: string;
    readonly episodeId: string;
    readonly at: string;
    readonly resumePath: string;
    readonly coverLetterPath: string;
  }[];
};

export type WorkflowDraftPreview = {
  readonly resume: string;
  readonly coverLetter: string;
  readonly notClaimed: readonly string[];
};

export type WorkflowEpisodeView = {
  readonly episodeId: string;
  readonly classification: EpisodeClassification;
  readonly outcome: EpisodeOutcomeKind;
  readonly confidence: "high" | "medium" | "low";
  readonly rationale: string;
  readonly startedAt: string;
  readonly endedAt: string;
  readonly caseId: string | null;
  readonly company: string | null;
  readonly position: string | null;
  readonly evidence: readonly EpisodeEvidence[];
  readonly locked: boolean;
  readonly uncertain: boolean;
};

export type WorkflowView = {
  readonly settings: WorkflowSettings;
  readonly host: WorkflowHostStatus;
  readonly sessionSites: readonly string[];
  readonly current: {
    readonly application: string | null;
    readonly title: string | null;
    readonly since: string | null;
    readonly classification: EpisodeClassification | null;
    readonly episodeId: string | null;
    readonly uncertain: boolean;
  } | null;
  readonly episodes: readonly WorkflowEpisodeView[];
  readonly proposals: readonly WorkflowProposal[];
  readonly reflex: WorkflowReflexRecord | null;
  readonly shadowPreview: WorkflowDraftPreview | null;
  readonly lastDraft: {
    readonly resumePath: string;
    readonly coverLetterPath: string;
    readonly receiptId: string;
  } | null;
  readonly judgment: {
    readonly episodeId: string;
    readonly options: readonly string[];
    readonly selected: string | null;
    readonly probabilities: Readonly<Record<string, number>>;
    readonly threshold: number;
    readonly reasonCode: string;
  } | null;
};
