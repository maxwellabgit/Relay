import type { ConnectorActionRef, ConnectorRef, ReflexRef } from "./artifacts.js";

export type FeedItemSnapshot = {
  readonly itemId: string;
  readonly kind: string;
  readonly summary: string;
  readonly createdAt: string;
  readonly caseId?: string;
};

/** Durable feed row — prose lives in the artifact store, not SQLite. */
export type FeedItemRecord = {
  readonly itemId: string;
  readonly kind: string;
  readonly contentArtifactId: string;
  readonly contentSha256: string;
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

export type JevDisclosureView = {
  readonly grantId: string;
  readonly scopeKind: "session" | "project";
  readonly scopeId: string;
  readonly expiresAt: string;
  readonly requestsUsed: number;
  readonly maxRequests: number;
  readonly bytesUsed: number;
  readonly maxBytes: number;
  readonly allowedSourceClasses: readonly string[];
};

export type RelaySnapshot = {
  readonly listening: boolean;
  /** Master off switch for hosted Jev. On is not a disclosure grant. */
  readonly hostedProcessingEnabled: boolean;
  /** Active session grant. Absent when missing, revoked, or expired. */
  readonly jevDisclosure?: JevDisclosureView | null;
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
  readonly decision: DecisionRunView | null;
  /** Compact Case execution projection from canonical trace events. */
  readonly caseExecution: CaseExecutionView | null;
  readonly currentInputPreview: string | null;
  readonly patterns: readonly PatternView[];
  readonly review: ReviewStatus;
  readonly trace: readonly TraceRow[];
  readonly memories: readonly MemoryView[];
  readonly actions: readonly ActionCard[];
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
  readonly deadLetters: number;
  readonly storageAdapter: string;
  readonly activeCaseId: string | null;
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
  readonly thresholds: Readonly<Record<string, number>>;
  readonly threshold: number | null;
  readonly optionLabels: Readonly<Record<string, string>>;
  readonly result: "pass" | "fail" | "wait" | "not_applicable" | "blocked";
  readonly reasonCode: string;
  readonly provider: string;
  readonly latencyMs: number | null;
  readonly retries: number;
  readonly nextAction: string;
  readonly receiptId: string | null;
  readonly decisionId: string | null;
  readonly caseId: string | null;
  readonly judgmentId: string | null;
  readonly selectedOptionId: string | null;
};

export type DecisionStageState = "not_started" | "running" | "passed" | "failed" | "waiting" | "skipped";

export type DecisionStageView = {
  readonly stage: string;
  readonly title: string;
  readonly state: DecisionStageState;
  readonly durationMs: number | null;
  readonly reasonCode: string | null;
  readonly branch?: {
    readonly continueLabel: string;
    readonly exitLabel: string;
    readonly taken: "continue" | "exit";
  };
};

export type DecisionAttemptView = {
  readonly attempt: number;
  readonly status: string;
  readonly reasonCode: string | null;
  readonly durationMs: number | null;
  readonly at: string;
  readonly providerRequestId?: string | null;
  readonly httpStatus?: number | null;
  readonly disclosureGrantId?: string | null;
  readonly retryDelayMs?: number | null;
};

export type DecisionRunView = {
  readonly decisionId: string;
  readonly receiptId: string | null;
  readonly caseId: string;
  readonly judgmentId: string | null;
  readonly reflexId: string | null;
  readonly gateId: string;
  readonly policyVersion: string;
  readonly historical: boolean;
  readonly questionType: DecisionReceiptView["questionType"];
  readonly status: DecisionStageState | "completed" | "blocked";
  readonly stages: readonly DecisionStageView[];
  readonly optionIds: readonly string[];
  readonly optionLabels: Readonly<Record<string, string>>;
  readonly probabilities: Readonly<Record<string, number>>;
  readonly thresholds: Readonly<Record<string, number>>;
  readonly topProbability: number | null;
  readonly margin: number | null;
  readonly selectedOptionId: string | null;
  readonly reasonCode: string;
  readonly nextAction: string;
  readonly provider: string;
  readonly attempts: readonly DecisionAttemptView[];
  readonly requestedAt: string | null;
  readonly completedAt: string | null;
  readonly elapsedMs: number | null;
  readonly result: DecisionReceiptView["result"];
};

/** One step in a Case execution explanation (not a graph / ledger). */
export type CaseExecutionStepView = {
  readonly key: string;
  readonly label: string;
  readonly state: "passed" | "failed" | "waiting" | "skipped" | "running";
  /** Delta from previous listed step; 0 for the first step. Null when skipped or unknown. */
  readonly deltaMs: number | null;
  /** Wall duration for this stage when the event carried durationMs. */
  readonly durationMs: number | null;
};

/**
 * Compact Case timing projected from canonical trace events.
 * Total is source.accepted → answer.committed when an answer exists.
 */
export type CaseExecutionView = {
  readonly caseId: string;
  readonly historical: boolean;
  readonly steps: readonly CaseExecutionStepView[];
  /** End-to-end ms when answer.committed is present; otherwise null. */
  readonly totalMs: number | null;
  /** User-facing outcome when there is no answer duration. */
  readonly outcome:
    | "answered"
    | "in_progress"
    | "resolved_without_answer"
    | "failed"
    | "blocked"
    | null;
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
  readonly candidateId: string | null;
  readonly because: string;
  readonly needed: string;
};

export type ReviewStatus = {
  readonly completeSessions: number;
  readonly sessionTrigger: number;
  readonly approvedCandidates: number;
  readonly builtReflexes: number;
  readonly activeReflexes: number;
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
  readonly stage: string | null;
  readonly status: string | null;
  readonly reasonCode: string | null;
  readonly latencyMs: number | null;
  readonly durationMs: number | null;
  readonly attempt: number | null;
  readonly caseId: string | null;
  readonly episodeId: string | null;
  readonly runId: string | null;
  readonly captureSessionId: string | null;
  readonly workSessionId: string | null;
  readonly decisionId: string | null;
  readonly judgmentId: string | null;
  readonly receiptId: string | null;
  readonly reflexId: string | null;
  readonly result: string | null;
  readonly toolId: string | null;
  readonly queueDepth: number | null;
  readonly providerRequestId?: string | null;
  readonly httpStatus?: number | null;
  readonly disclosureGrantId?: string | null;
  readonly retryDelayMs?: number | null;
};

export type ActionCard = {
  readonly actionId: string;
  readonly kind:
    | "confirm_birthday"
    | "save_definition"
    | "replace_memory"
    | "ambient_recommendation";
  readonly label: string;
  readonly token?: string;
  readonly expansion?: string;
  readonly personId?: string;
  readonly displayName?: string;
  readonly date?: string;
  /** Ambient recommendation fields (kind === ambient_recommendation). */
  readonly recommendationId?: string;
  readonly candidateEventId?: string;
  readonly caseId?: string;
  readonly title?: string;
  readonly reason?: string;
  readonly evidenceCount?: number;
  readonly primary?: "save" | "verify" | "create_task" | "review";
  readonly quiet?: boolean;
  readonly noteKey?: string;
  readonly noteText?: string;
};

export type MemoryView = {
  readonly kind: "glossary" | "birthday" | "note" | "fact" | "recommendation";
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
