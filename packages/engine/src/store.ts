import type {
  CaseKind,
  CaseOrigin,
  CasePhase,
  CaseRecord,
  CaseStatus,
  DataPolicy,
  FeedItemSnapshot,
  JudgmentRecord,
  RelaySnapshot,
  TranscriptSegmentV1,
} from "@relay/contracts";
import type { WorkItem } from "./queue.js";

export type PersistedSourceEvent = {
  readonly sourceEventId: string;
  readonly sessionId: string;
  readonly segment: TranscriptSegmentV1;
  readonly textArtifactId: string;
  readonly textSha256: string;
  readonly policy: DataPolicy;
  readonly createdAt: string;
};

export type EngineStore = {
  ensureSession(sessionId: string, createdAt: string): Promise<void>;
  setListening(sessionId: string, listening: boolean): Promise<void>;
  getListening(sessionId: string): Promise<boolean>;
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
  listFeedItems(): Promise<readonly FeedItemSnapshot[]>;
  addFeedItem(item: FeedItemSnapshot): Promise<void>;
  listSourceSegments(sessionId: string): Promise<RelaySnapshot["sourceSegments"]>;
  enqueue(item: WorkItem): Promise<void>;
  claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null>;
  complete(workId: string): Promise<void>;
  requeue(workId: string, availableAt: string): Promise<void>;
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
};
