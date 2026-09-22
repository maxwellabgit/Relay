import type {
  ConnectorActionRef,
  ConnectorRef,
  JudgmentDefinitionRef,
  ReflexRef,
} from "./artifacts.js";
import type { SourceSliceRef } from "./transcript.js";
import type { ReflexActivationState, ReflexRunSummary } from "./changes.js";

export type ApprovalMode = "always_ask" | "explicit_utterance" | "standing_grant_eligible";
export type RollbackStrategy = "none" | "compensating_action" | "soft_disable";

export type ReflexBudgets = {
  readonly maxSourceAttempts: number;
  readonly maxJudgmentRounds: number;
  readonly maxHostedTokens: number;
};

export type ReflexRetryPolicy = {
  readonly maxAttempts: number;
  readonly initialBackoffMs: number;
  readonly maxBackoffMs: number;
};

export type ReflexRollbackPolicy = {
  readonly strategy: RollbackStrategy;
  readonly compensatingAction?: ConnectorActionRef;
  readonly notes?: string;
};

export type ReflexDefinition = {
  readonly id: string;
  readonly version: number;
  readonly displayName: string;
  readonly triggers: readonly string[];
  readonly negativeTriggers: readonly string[];
  readonly conditions: readonly string[];
  readonly permittedSources: readonly ConnectorRef[];
  readonly readPlan: readonly ConnectorActionRef[];
  readonly judgments: readonly JudgmentDefinitionRef[];
  readonly permittedWriteActions: readonly ConnectorActionRef[];
  readonly approvalMode: ApprovalMode;
  readonly budgets: ReflexBudgets;
  readonly retryPolicy: ReflexRetryPolicy;
  readonly evaluationFixtureIds: readonly string[];
  readonly explanationTemplate: string;
  readonly defaultActivation: ReflexActivationState;
  readonly rollback: ReflexRollbackPolicy;
};

export type ConnectionView = {
  readonly connectionId: string;
  readonly connectionVersion: number;
  readonly connector: ConnectorRef;
  readonly connected: boolean;
  readonly observationEnabled: boolean;
  readonly selectedResources: readonly string[];
  readonly readScopes: readonly string[];
  readonly writeActionEnabled: Readonly<Record<string, boolean>>;
  readonly grantedOAuthScopes: readonly string[];
};

export type ReflexContext = {
  readonly caseId: string;
  readonly caseVersion: number;
  readonly reflex: ReflexRef;
  readonly triggerSourceRefs: readonly SourceSliceRef[];
  readonly eligibleConnections: readonly ConnectionView[];
  readonly remainingBudgets: ReflexBudgets;
  readonly now: string;
  /** Observation text for detectors that need the current window. */
  readonly observationText?: string;
  readonly triggerToken?: string;
  readonly isExplicitAsk?: boolean;
};

export type DetectionContext = {
  readonly sessionId: string;
  readonly now: string;
  readonly listening: boolean;
};

export type SourceEvent = {
  readonly sourceEventId: string;
  readonly segmentId: string;
  readonly text: string;
  readonly origin: string;
  readonly speakerKey: string | null;
  readonly startMs: number;
  readonly endMs: number;
};

export type TriggerCandidate = {
  readonly reflexId: string;
  readonly reflexVersion: number;
  readonly token: string;
  readonly start: number;
  readonly end: number;
  readonly reason: string;
};

export type ReflexEvidenceDraft = {
  readonly kind: string;
  readonly summary: string;
  readonly sourceRefs: readonly SourceSliceRef[];
};

export type OperationPrecondition =
  | { readonly kind: "ProviderRevisionEquals"; readonly expectedRevision: string }
  | { readonly kind: "ResourceExists"; readonly resourceId: string }
  | { readonly kind: "ResourceMissing"; readonly resourceId: string }
  | {
      readonly kind: "FieldEquals";
      readonly fieldPath: string;
      readonly expectedValue: string;
    }
  | {
      readonly kind: "EquivalentCalendarEventAbsent";
      readonly personKey: string;
      readonly monthDay: string;
    };

export type OperationProposal = {
  readonly connectionId: string;
  readonly action: ConnectorActionRef;
  readonly arguments: unknown;
  readonly inputRefs: readonly SourceSliceRef[];
  readonly requestedResourceScope: unknown;
  readonly preconditions: readonly OperationPrecondition[];
  readonly suggestedIdempotencyKey?: string;
};

export type ReadRequest = {
  readonly connectionId: string;
  readonly action: ConnectorActionRef;
  readonly arguments: unknown;
  readonly inputRefs: readonly SourceSliceRef[];
};

export type JudgmentRequestDraft = {
  readonly questionSetId: string;
  readonly questionSetVersion: string;
  readonly questions: Readonly<Record<string, unknown>>;
  readonly state: unknown;
};

type ReflexResultBase = {
  readonly summary: string;
  readonly sourceRefs: readonly SourceSliceRef[];
  readonly judgmentIds: readonly string[];
};

export type FindingResult = ReflexResultBase & {
  readonly type: "finding";
  readonly evidenceDrafts: readonly ReflexEvidenceDraft[];
};

export type ReadRequestedResult = ReflexResultBase & {
  readonly type: "read_requested";
  readonly readRequests: readonly ReadRequest[];
};

export type OperationProposedResult = ReflexResultBase & {
  readonly type: "operation_proposed";
  readonly operationProposals: readonly OperationProposal[];
};

export type ClarificationRequiredResult = ReflexResultBase & {
  readonly type: "clarification_required";
  readonly clarificationPrompt: string;
};

export type NoActionResult = ReflexResultBase & {
  readonly type: "no_action";
};

export type ReflexResult =
  | FindingResult
  | ReadRequestedResult
  | OperationProposedResult
  | ClarificationRequiredResult
  | NoActionResult;

export type ReflexModule = {
  readonly definition: ReflexDefinition;
  detect(event: SourceEvent, context: DetectionContext): TriggerCandidate[];
  evaluate(context: ReflexContext): Promise<ReflexResult>;
};

export type ReflexState = {
  readonly reflex: ReflexRef;
  readonly stateVersion: number;
  readonly activation: ReflexActivationState;
  readonly runs: ReflexRunSummary;
};
