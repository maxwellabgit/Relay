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
};

export type ActivityLine = {
  readonly sequence: number;
  readonly at: string;
  readonly eventType: string;
  readonly message: string;
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
