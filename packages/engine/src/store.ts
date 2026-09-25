import type {
  CandidateEvent,
  CandidateEventStatus,
  CaseKind,
  CaseOrigin,
  CasePhase,
  CaseRecord,
  CaseStatus,
  DataPolicy,
  FeedItemRecord,
  JudgmentRecord,
  RelaySnapshot,
  TranscriptSegmentV1,
} from "@relay/contracts";
import type { WorkItem } from "./queue.js";
import type { LearningStore } from "./learning-store.js";
import type { DisclosureScope, HostedJudgmentGrant } from "./disclosure/hosted-grant.js";
import type { GrantReservation } from "./disclosure/grant-account.js";

export type PersistedSourceEvent = {
  readonly sourceEventId: string;
  readonly sessionId: string;
  readonly segment: TranscriptSegmentV1;
  readonly textArtifactId: string;
  readonly textSha256: string;
  readonly policy: DataPolicy;
  readonly createdAt: string;
};

export const HOSTED_PROCESSING_SETTING_KEY = "hosted_processing_enabled";

export type EngineStore = {
  ensureSession(sessionId: string, createdAt: string): Promise<void>;
  setListening(sessionId: string, listening: boolean): Promise<void>;
  getListening(sessionId: string): Promise<boolean>;
  /** Application grant; default false. Independent of Listening. */
  getHostedProcessingEnabled(): Promise<boolean>;
  setHostedProcessingEnabled(enabled: boolean): Promise<void>;
  persistFinalSource(event: PersistedSourceEvent): Promise<{ inserted: boolean }>;
  createCase(input: {
    caseId: string;
    origin: CaseOrigin;
    kind: CaseKind;
    priority: number;
    parentCaseId?: string;
    at: string;
  }): Promise<CaseRecord>;
  getCase(caseId: string): Promise<CaseRecord | null>;
  updateCase(
    caseId: string,
    expectedVersion: number,
    patch: {
      status?: CaseStatus;
      phase?: CasePhase;
      waitKind?: string | null;
      at: string;
    },
  ): Promise<CaseRecord | null>;
  appendCaseEvent(
    caseId: string,
    caseVersion: number,
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<void>;
  listActiveCases(): Promise<readonly CaseRecord[]>;
  listFeedItemRecords(): Promise<readonly FeedItemRecord[]>;
  addFeedItem(item: FeedItemRecord): Promise<void>;
  listSourceSegments(sessionId: string): Promise<RelaySnapshot["sourceSegments"]>;
  enqueue(item: WorkItem): Promise<void>;
  claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null>;
  complete(workId: string): Promise<void>;
  requeue(workId: string, availableAt: string, payload?: Record<string, unknown>): Promise<void>;
  upsertJudgment(record: JudgmentRecord): Promise<void>;
  findCompletedJudgmentByHash(requestHash: string): Promise<JudgmentRecord | null>;
  appendDomainEvent(type: string, at: string, payload: Record<string, unknown>): Promise<number>;
  listDomainEvents(limit: number): Promise<
    readonly {
      sequence: number;
      type: string;
      at: string;
      payload: Record<string, unknown>;
    }[]
  >;
  countWorkItems(): Promise<number>;
  deadLetter(workId: string, reasonCode: string, at: string): Promise<void>;
  listDeadLetters(): Promise<readonly { workId: string; reasonCode: string; at: string }[]>;
  upsertJudgmentAttempt(record: {
    readonly attemptId: string;
    readonly caseId: string;
    readonly workId?: string;
    readonly attempt: number;
    readonly maxAttempts: number;
    readonly nextAttemptAt?: string | null;
    readonly failureCategory?: string | null;
    readonly providerRequestId?: string | null;
    readonly createdAt: string;
  }): Promise<void>;
  /**
   * Optional atomic multi-step boundary. When absent, callers use
   * {@link runInTransaction} which falls through to the callback.
   */
  runInTransaction?<T>(work: () => Promise<T>): Promise<T>;
  putCandidateEvent(event: CandidateEvent): Promise<void>;
  getCandidateEvent(candidateEventId: string): Promise<CandidateEvent | null>;
  listCandidateEvents(caseId?: string): Promise<readonly CandidateEvent[]>;
  updateCandidateEventStatus(
    candidateEventId: string,
    status: CandidateEventStatus,
    updatedAt: string,
  ): Promise<void>;
  putAmbientSuppression(key: string, reason: string, createdAt: string): Promise<void>;
  isAmbientSuppressed(key: string): Promise<boolean>;
  readonly learning: LearningStore;
  saveHostedGrant?(grant: HostedJudgmentGrant, at: string): Promise<void>;
  revokeHostedGrant?(grantId: string, at: string): Promise<void>;
  findHostedGrant?(grantId: string): Promise<HostedJudgmentGrant | null>;
  readHostedGrant?(scope: DisclosureScope): Promise<{
    grant: HostedJudgmentGrant | null;
    requestsUsed: number;
    bytesUsed: number;
  }>;
  reserveHostedGrant?(input: {
    grantId: string;
    bytes: number;
    now: string;
    reservationId: string;
  }): Promise<GrantReservation>;
  commitHostedGrant?(reservationId: string): Promise<void>;
  releaseHostedGrant?(reservationId: string): Promise<void>;
  releaseUncommittedHostedGrants?(): Promise<number>;
  putFoundation?(kind: string, id: string, version: number, payload: unknown, at: string): Promise<void>;
  getFoundation?(kind: string, id: string): Promise<{ id: string; version: number; payload: unknown; updatedAt: string } | null>;
  listFoundation?(kind: string): Promise<readonly { id: string; version: number; payload: unknown; updatedAt: string }[]>;
  linkExecutionCase?(executionId: string, projectCaseId: string, at: string): Promise<void>;
  listExecutionCases?(executionId: string): Promise<readonly string[]>;
  claimFoundation?(kind: string, id: string, version: number, payload: unknown, at: string): Promise<boolean>;
};
