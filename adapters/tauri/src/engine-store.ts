import type {
  CaseKind,
  CaseOrigin,
  CasePhase,
  CaseRecord,
  CaseStatus,
  FeedItemRecord,
  JudgmentRecord,
  RelaySnapshot,
} from "@relay/contracts";
import type {
  CandidateRecord,
  EngineStore,
  EpisodeRecord,
  LearningStore,
  MemoryKind,
  MemoryRecord,
  PatternRecord,
  PersistedSourceEvent,
  ReceiptRecord,
  ReviewRecord,
  WorkItem,
  WorkSessionRecord,
} from "@relay/engine";

export type StoreInvoke = (command: string, args: { readonly op: Record<string, unknown> }) => Promise<unknown>;

type Call = (op: Record<string, unknown>) => Promise<unknown>;

export class TauriEngineStore implements EngineStore {
  readonly learning: LearningStore;

  constructor(private readonly invoke: StoreInvoke) {
    const call: Call = (op) => this.invoke("store_execute", { op });
    this.learning = new TauriLearning(call);
    this.call = call;
  }

  private readonly call: Call;

  ensureSession(sessionId: string, createdAt: string): Promise<void> {
    return this.voidOp({ op: "ensure_session", sessionId, createdAt });
  }

  setListening(sessionId: string, listening: boolean): Promise<void> {
    return this.voidOp({ op: "set_listening", sessionId, listening });
  }

  async getListening(sessionId: string): Promise<boolean> {
    return (await this.call({ op: "get_listening", sessionId })) === true;
  }

  async persistFinalSource(event: PersistedSourceEvent): Promise<{ inserted: boolean }> {
    const value = await this.call({ op: "persist_final_source", event });
    const inserted = typeof value === "object" && value !== null && "inserted" in value && value.inserted === true;
    return { inserted };
  }

  async createCase(input: {
    caseId: string;
    origin: CaseOrigin;
    kind: CaseKind;
    priority: number;
    parentCaseId?: string;
    at: string;
  }): Promise<CaseRecord> {
    return (await this.call({ op: "create_case", ...input })) as CaseRecord;
  }

  async getCase(caseId: string): Promise<CaseRecord | null> {
    return (await this.call({ op: "get_case", caseId })) as CaseRecord | null;
  }

  async updateCase(
    caseId: string,
    expectedVersion: number,
    patch: { status?: CaseStatus; phase?: CasePhase; waitKind?: string | null; at: string },
  ): Promise<CaseRecord | null> {
    return (await this.call({ op: "update_case", caseId, expectedVersion, patch })) as CaseRecord | null;
  }

  appendCaseEvent(
    caseId: string,
    caseVersion: number,
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<void> {
    return this.voidOp({ op: "append_case_event", caseId, caseVersion, type, at, payload });
  }

  async listActiveCases(): Promise<readonly CaseRecord[]> {
    return (await this.call({ op: "list_active_cases" })) as CaseRecord[];
  }

  async listFeedItemRecords(): Promise<readonly FeedItemRecord[]> {
    return (await this.call({ op: "list_feed_items" })) as FeedItemRecord[];
  }

  addFeedItem(item: FeedItemRecord): Promise<void> {
    return this.voidOp({ op: "add_feed_item", item });
  }

  async listSourceSegments(sessionId: string): Promise<RelaySnapshot["sourceSegments"]> {
    return (await this.call({ op: "list_source_segments", sessionId })) as RelaySnapshot["sourceSegments"];
  }

  enqueue(item: WorkItem): Promise<void> {
    return this.voidOp({ op: "enqueue", item });
  }

  async claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null> {
    return (await this.call({ op: "claim_next", now, owner, leaseMs })) as WorkItem | null;
  }

  complete(workId: string): Promise<void> {
    return this.voidOp({ op: "complete", workId });
  }

  requeue(workId: string, availableAt: string, payload?: Record<string, unknown>): Promise<void> {
    return this.voidOp({
      op: "requeue",
      workId,
      availableAt,
      ...(payload ? { payload } : {}),
    });
  }

  upsertJudgment(record: JudgmentRecord): Promise<void> {
    return this.voidOp({ op: "upsert_judgment", record });
  }

  async findCompletedJudgmentByHash(requestHash: string): Promise<JudgmentRecord | null> {
    return (await this.call({ op: "find_completed_judgment", requestHash })) as JudgmentRecord | null;
  }

  async appendDomainEvent(type: string, at: string, payload: Record<string, unknown>): Promise<number> {
    return Number(await this.call({ op: "append_domain_event", type, at, payload }));
  }

  async listDomainEvents(limit: number): Promise<
    readonly { sequence: number; type: string; at: string; payload: Record<string, unknown> }[]
  > {
    return (await this.call({ op: "list_domain_events", limit })) as {
      sequence: number;
      type: string;
      at: string;
      payload: Record<string, unknown>;
    }[];
  }

  async countWorkItems(): Promise<number> {
    return Number(await this.call({ op: "count_work_items" }));
  }

  deadLetter(workId: string, reasonCode: string, at: string): Promise<void> {
    return this.voidOp({ op: "dead_letter", workId, reasonCode, at });
  }

  async listDeadLetters(): Promise<readonly { workId: string; reasonCode: string; at: string }[]> {
    return (await this.call({ op: "list_dead_letters" })) as { workId: string; reasonCode: string; at: string }[];
  }

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
  }): Promise<void> {
    return this.voidOp({ op: "upsert_judgment_attempt", record });
  }

  private async voidOp(op: Record<string, unknown>): Promise<void> {
    await this.call(op);
  }
}

class TauriLearning implements LearningStore {
  constructor(private readonly call: Call) {}

  putMemory(record: MemoryRecord): Promise<void> {
    return this.voidOp({ op: "put_memory", record });
  }

  deleteMemory(kind: MemoryKind, key: string): Promise<void> {
    return this.voidOp({ op: "delete_memory", kind, key });
  }

  async getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null> {
    return (await this.call({ op: "get_memory", kind, key })) as MemoryRecord | null;
  }

  async listMemories(): Promise<readonly MemoryRecord[]> {
    return (await this.call({ op: "list_memories" })) as MemoryRecord[];
  }

  openSession(record: WorkSessionRecord): Promise<void> {
    return this.voidOp({ op: "open_session", record });
  }

  closeSession(
    sessionId: string,
    endedAt: string,
    termination: "completed" | "abandoned",
    episodeCount: number,
  ): Promise<void> {
    return this.voidOp({ op: "close_session", sessionId, endedAt, termination, episodeCount });
  }

  async currentSession(): Promise<WorkSessionRecord | null> {
    return (await this.call({ op: "current_session" })) as WorkSessionRecord | null;
  }

  async listSessions(): Promise<readonly WorkSessionRecord[]> {
    return (await this.call({ op: "list_sessions" })) as WorkSessionRecord[];
  }

  putEpisode(record: EpisodeRecord): Promise<void> {
    return this.voidOp({ op: "put_episode", record });
  }

  async listEpisodes(): Promise<readonly EpisodeRecord[]> {
    return (await this.call({ op: "list_episodes" })) as EpisodeRecord[];
  }

  putReceipt(record: ReceiptRecord): Promise<void> {
    return this.voidOp({ op: "put_receipt", record });
  }

  async listReceipts(): Promise<readonly ReceiptRecord[]> {
    return (await this.call({ op: "list_receipts" })) as ReceiptRecord[];
  }

  putPattern(record: PatternRecord): Promise<void> {
    return this.voidOp({ op: "put_pattern", record });
  }

  async getPattern(signature: string): Promise<PatternRecord | null> {
    return (await this.call({ op: "get_pattern", signature })) as PatternRecord | null;
  }

  async listPatterns(): Promise<readonly PatternRecord[]> {
    return (await this.call({ op: "list_patterns" })) as PatternRecord[];
  }

  putCandidate(record: CandidateRecord): Promise<void> {
    return this.voidOp({ op: "put_candidate", record });
  }

  async listCandidates(): Promise<readonly CandidateRecord[]> {
    return (await this.call({ op: "list_candidates" })) as CandidateRecord[];
  }

  putReview(record: ReviewRecord): Promise<void> {
    return this.voidOp({ op: "put_review", record });
  }

  async listReviews(): Promise<readonly ReviewRecord[]> {
    return (await this.call({ op: "list_reviews" })) as ReviewRecord[];
  }

  async compact(nowIso: string): Promise<number> {
    return Number(await this.call({ op: "compact", nowIso }));
  }

  private async voidOp(op: Record<string, unknown>): Promise<void> {
    await this.call(op);
  }
}
