import type { ConnectorActionRef, ConnectorRef, ReflexRef } from "./artifacts.js";

export type FeedItemSnapshot = {
  readonly itemId: string;
  readonly kind: string;
  readonly summary: string;
  readonly createdAt: string;
  readonly caseId?: string;
};

export type ApprovalSnapshot = {
  readonly operationId: string;
  readonly action: ConnectorActionRef;
  readonly summary: string;
  readonly canonicalHash: string;
  readonly caseVersion: number;
  readonly connectionId: string;
  readonly connectionVersion: number;
  readonly proposedAt: string;
};

export type ConnectionStateSnapshot = {
  readonly connectionId: string;
  readonly connectionVersion: number;
  readonly connector: ConnectorRef;
  readonly connected: boolean;
  readonly observationEnabled: boolean;
  readonly healthStatus: string;
  readonly selectedResources: readonly string[];
  readonly writeActionEnabled: Readonly<Record<string, boolean>>;
  readonly grantedOAuthScopes: readonly string[];
};

export type ReflexActivationState = "inactive" | "active" | "paused";

export type ReflexRunSummary = {
  readonly runCount: number;
  readonly successCount: number;
  readonly failureCount: number;
  readonly lastRunAt?: string;
};

export type ReflexStateSnapshot = {
  readonly reflex: ReflexRef;
  readonly stateVersion: number;
  readonly activation: ReflexActivationState;
  readonly runs: ReflexRunSummary;
};

export type ProviderHealthSnapshot = {
  readonly providerId: string;
  readonly ok: boolean;
  readonly status: string;
};

export type WaitSnapshot = {
  readonly caseId: string;
  readonly waitKind: string;
  readonly dueAt?: string;
};

export type StatusChip =
  | "engine"
  | "model"
  | "jev"
  | "audio"
  | "halo"
  | "listening"
  | "storage";

export type StatusChipState = {
  readonly id: StatusChip;
  readonly label: string;
  readonly ok: boolean;
  readonly detail: string;
};

export type RelaySnapshot = {
  readonly listening: boolean;
  readonly activeCaseId?: string;
  readonly feedItems: readonly FeedItemSnapshot[];
  readonly approvals: readonly ApprovalSnapshot[];
  readonly connections: readonly ConnectionStateSnapshot[];
  readonly reflexes: readonly ReflexStateSnapshot[];
  readonly providerHealth: readonly ProviderHealthSnapshot[];
  readonly waits: readonly WaitSnapshot[];
  readonly status: readonly StatusChipState[];
  readonly sourceSegments: readonly SourceSegmentView[];
  readonly cases: readonly CaseView[];
  readonly queueDepth: number;
  readonly activity: readonly ActivityLine[];
  readonly runtime: RuntimeHeader;
  readonly gate: DecisionReceiptView | null;
  readonly patterns: readonly PatternView[];
  readonly review: ReviewStatus;
  readonly trace: readonly TraceRow[];
  readonly memories: readonly MemoryView[];
};

export type ActivityLine = {
  readonly sequence: number;
  readonly at: string;
  readonly eventType: string;
  readonly message: string;
};

export type RuntimeHeader = {
  readonly runId: string;
  readonly commit: string;
  readonly sessionId: string | null;
  readonly episodeId: string | null;
  readonly queueDepth: number;
  readonly logPath: string;
  readonly logWritable: boolean;
  readonly logError: string | null;
  readonly mode: "live" | "recorded" | "replay";
  readonly retention: string;
};

export type DecisionReceiptView = {
  readonly at: string;
  readonly gateId: string;
  readonly policyVersion: string;
  readonly reflexId: string | null;
  readonly questionType: "deterministic" | "choice" | "noul" | "user" | "not_applicable";
  readonly optionIds: readonly string[];
  readonly probabilities: Readonly<Record<string, number>>;
  readonly topProbability: number | null;
  readonly margin: number | null;
  readonly threshold: number | null;
  readonly result: "pass" | "fail" | "wait" | "not_applicable";
  readonly reasonCode: string;
  readonly provider: string;
  readonly latencyMs: number | null;
  readonly retries: number;
  readonly nextAction: string;
};

export type PatternView = {
  readonly signature: string;
  readonly count: number;
  readonly sessions: number;
  readonly outcomes: Readonly<Record<string, number>>;
  readonly firstAt: string;
  readonly lastAt: string;
  readonly evidenceIds: readonly string[];
  readonly candidateState: string | null;
  readonly because: string;
  readonly needed: string;
};

export type ReviewStatus = {
  readonly completeSessions: number;
  readonly sessionTrigger: number;
  readonly approvedReflexes: number;
  readonly reflexTrigger: number;
  readonly completeEpisodes: number;
  readonly episodeTrigger: number;
  readonly qualifiedCandidates: number;
  readonly candidateTrigger: number;
  readonly reviewDue: boolean;
  readonly trigger: string | null;
};

export type TraceRow = {
  readonly sequence: number;
  readonly at: string;
  readonly type: string;
  readonly reasonCode: string | null;
  readonly latencyMs: number | null;
  readonly caseId: string | null;
  readonly result: string | null;
};

export type MemoryView = {
  readonly kind: "glossary" | "birthday";
  readonly key: string;
  readonly fields: Readonly<Record<string, string>>;
};

export type SourceSegmentView = {
  readonly segmentId: string;
  readonly speakerKey: string | null;
  readonly text: string;
  readonly final: boolean;
  readonly origin: string;
  readonly sequence: number;
};

export type CaseView = {
  readonly caseId: string;
  readonly phase: string;
  readonly status: string;
  readonly origin: string;
  readonly version: number;
};

export type SnapshotReplacedChange = {
  readonly type: "SnapshotReplaced";
  readonly snapshot: RelaySnapshot;
};

export type FeedItemAddedChange = {
  readonly type: "FeedItemAdded";
  readonly item: FeedItemSnapshot;
};

export type ApprovalChangedChange = {
  readonly type: "ApprovalChanged";
  readonly operationId: string;
  readonly removed: boolean;
  readonly approval?: ApprovalSnapshot;
};

export type ListeningChangedChange = {
  readonly type: "ListeningChanged";
  readonly listening: boolean;
};

export type ConnectionChangedChange = {
  readonly type: "ConnectionChanged";
  readonly connection: ConnectionStateSnapshot;
};

export type ReflexChangedChange = {
  readonly type: "ReflexChanged";
  readonly reflex: ReflexStateSnapshot;
};

export type TraceAppendedChange = {
  readonly type: "TraceAppended";
  readonly sequence: number;
  readonly eventType: string;
  readonly message: string;
  readonly at: string;
};

export type RelayChange =
  | SnapshotReplacedChange
  | FeedItemAddedChange
  | ApprovalChangedChange
  | ListeningChangedChange
  | ConnectionChangedChange
  | ReflexChangedChange
  | TraceAppendedChange;
