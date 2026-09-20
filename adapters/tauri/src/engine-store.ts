import type { ArtifactStorePort, CaseKind, CaseOrigin, CasePhase, CaseRecord, CaseStatus, FeedItemRecord, JudgmentRecord, RelaySnapshot } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
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
import {
  getJsonArtifact,
  labelsOrUnavailable,
  packMemoryValue,
  putJsonArtifact,
  unpackMemoryValue,
} from "@relay/engine";

export type StoreInvoke = (command: string, args: { readonly op: Record<string, unknown> }) => Promise<unknown>;

type Call = (op: Record<string, unknown>) => Promise<unknown>;

export class TauriEngineStore implements EngineStore {
  readonly learning: LearningStore;

  constructor(
    private readonly invoke: StoreInvoke,
    artifacts?: ArtifactStorePort,
  ) {
    const call: Call = (op) => this.invoke("store_execute", { op });
    this.learning = new TauriLearning(call, artifacts);
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

  async getHostedProcessingEnabled(): Promise<boolean> {
    return (await this.call({ op: "get_hosted_processing" })) === true;
  }

  setHostedProcessingEnabled(enabled: boolean): Promise<void> {
    return this.voidOp({ op: "set_hosted_processing", enabled });
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

type PersistedMemory = {
  readonly memoryId: string;
  readonly kind: MemoryKind;
  readonly key: string;
  readonly value: Readonly<Record<string, string>>;
  readonly source: MemoryRecord["source"];
  readonly createdAt: string;
  readonly contentArtifactId?: string | null;
  readonly contentSha256?: string | null;
  readonly metadata?: Readonly<Record<string, string>>;
};

type PersistedReceipt = ReceiptRecord & {
  readonly labelsArtifactId?: string | null;
  readonly labelsSha256?: string | null;
};

type PersistedCandidate = CandidateRecord & {
  readonly becauseArtifactId?: string | null;
  readonly becauseSha256?: string | null;
};

type PersistedReview = ReviewRecord & {
  readonly findingsArtifactId?: string | null;
  readonly findingsSha256?: string | null;
};

class TauriLearning implements LearningStore {
  constructor(
    private readonly call: Call,
    private readonly artifacts?: ArtifactStorePort,
  ) {}

  async putMemory(record: MemoryRecord): Promise<void> {
    if (!this.artifacts) {
      await this.voidOp({ op: "put_memory", record });
      return;
    }
    const packed = packMemoryValue(record.kind, record.value);
    const ref = await this.artifacts.put(
      new TextEncoder().encode(JSON.stringify(packed.prose)),
      localOnlyPolicy(),
    );
    const persisted: PersistedMemory = {
      memoryId: record.memoryId,
      kind: record.kind,
      key: record.key,
      value: {},
      source: record.source,
      createdAt: record.createdAt,
      contentArtifactId: ref.artifactId,
      contentSha256: ref.sha256,
      metadata: packed.metadata,
    };
    await this.voidOp({ op: "put_memory", record: persisted });
  }

  deleteMemory(kind: MemoryKind, key: string): Promise<void> {
    return this.voidOp({ op: "delete_memory", kind, key });
  }

  async getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null> {
    const row = (await this.call({ op: "get_memory", kind, key })) as PersistedMemory | null;
    return row ? this.hydrateMemory(row) : null;
  }

  async listMemories(): Promise<readonly MemoryRecord[]> {
    const rows = (await this.call({ op: "list_memories" })) as PersistedMemory[];
    return Promise.all(rows.map((row) => this.hydrateMemory(row)));
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

  async recordCompletedEpisode(record: EpisodeRecord): Promise<PatternRecord | null> {
    return (await this.call({ op: "record_completed_episode", record })) as PatternRecord | null;
  }

  async listEpisodes(): Promise<readonly EpisodeRecord[]> {
    return (await this.call({ op: "list_episodes" })) as EpisodeRecord[];
  }

  async putReceipt(record: ReceiptRecord): Promise<void> {
    if (!this.artifacts) {
      await this.voidOp({ op: "put_receipt", record });
      return;
    }
    const labels = record.optionLabels ?? {};
    let labelsArtifactId: string | null = null;
    let labelsSha256: string | null = null;
    if (Object.keys(labels).length > 0) {
      const ref = await putJsonArtifact(this.artifacts, labels);
      labelsArtifactId = ref.artifactId;
      labelsSha256 = ref.sha256;
    }
    const selectedOptionId = record.selectedOptionId ?? record.selectedOption;
    const persisted: PersistedReceipt = {
      ...record,
      optionLabels: {},
      selectedOption: selectedOptionId,
      selectedOptionId,
      labelsArtifactId,
      labelsSha256,
    };
    await this.voidOp({ op: "put_receipt", record: persisted });
  }

  async listReceipts(): Promise<readonly ReceiptRecord[]> {
    const rows = (await this.call({ op: "list_receipts" })) as PersistedReceipt[];
    return Promise.all(rows.map((row) => this.hydrateReceipt(row)));
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

  async putCandidate(record: CandidateRecord): Promise<void> {
    if (!this.artifacts) {
      await this.voidOp({ op: "put_candidate", record });
      return;
    }
    let becauseArtifactId: string | null = null;
    let becauseSha256: string | null = null;
    if (record.because) {
      const ref = await this.artifacts.put(new TextEncoder().encode(record.because), localOnlyPolicy());
      becauseArtifactId = ref.artifactId;
      becauseSha256 = ref.sha256;
    }
    const persisted: PersistedCandidate = {
      ...record,
      because: "",
      becauseArtifactId,
      becauseSha256,
    };
    await this.voidOp({ op: "put_candidate", record: persisted });
  }

  async listCandidates(): Promise<readonly CandidateRecord[]> {
    const rows = (await this.call({ op: "list_candidates" })) as PersistedCandidate[];
    return Promise.all(rows.map((row) => this.hydrateCandidate(row)));
  }

  async putReview(record: ReviewRecord): Promise<void> {
    if (!this.artifacts) {
      await this.voidOp({ op: "put_review", record });
      return;
    }
    let findingsArtifactId: string | null = null;
    let findingsSha256: string | null = null;
    if (record.findings.length > 0) {
      const ref = await putJsonArtifact(this.artifacts, record.findings);
      findingsArtifactId = ref.artifactId;
      findingsSha256 = ref.sha256;
    }
    const persisted: PersistedReview = {
      ...record,
      findings: [],
      findingsArtifactId,
      findingsSha256,
    };
    await this.voidOp({ op: "put_review", record: persisted });
  }

  async listReviews(): Promise<readonly ReviewRecord[]> {
    const rows = (await this.call({ op: "list_reviews" })) as PersistedReview[];
    return Promise.all(rows.map((row) => this.hydrateReview(row)));
  }

  async compact(nowIso: string): Promise<number> {
    return Number(await this.call({ op: "compact", nowIso }));
  }

  private async hydrateMemory(row: PersistedMemory): Promise<MemoryRecord> {
    if (!this.artifacts || !row.contentArtifactId || !row.contentSha256) {
      return {
        memoryId: row.memoryId,
        kind: row.kind,
        key: row.key,
        value: row.value ?? {},
        source: row.source,
        createdAt: row.createdAt,
      };
    }
    let prose: Record<string, string> = {};
    try {
      const bytes = await this.artifacts.get({
        artifactId: row.contentArtifactId,
        sha256: row.contentSha256,
        policy: localOnlyPolicy(),
      });
      prose = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, string>;
    } catch {
      prose = {};
    }
    return {
      memoryId: row.memoryId,
      kind: row.kind,
      key: row.key,
      value: unpackMemoryValue(row.kind, prose, row.metadata ?? {}),
      source: row.source,
      createdAt: row.createdAt,
    };
  }

  private async hydrateReceipt(row: PersistedReceipt): Promise<ReceiptRecord> {
    const optionIds = Object.keys(row.probabilities ?? {});
    let optionLabels = row.optionLabels ?? {};
    if (this.artifacts && row.labelsArtifactId && row.labelsSha256) {
      try {
        optionLabels = await getJsonArtifact(this.artifacts, {
          artifactId: row.labelsArtifactId,
          sha256: row.labelsSha256,
          policy: localOnlyPolicy(),
        });
      } catch {
        optionLabels = labelsOrUnavailable(optionIds, null);
      }
    } else if (Object.keys(optionLabels).length === 0 && optionIds.length > 0) {
      optionLabels = labelsOrUnavailable(optionIds, null);
    }
    const selectedOptionId = row.selectedOptionId ?? row.selectedOption;
    return {
      ...row,
      optionLabels,
      selectedOption: selectedOptionId,
      selectedOptionId,
    };
  }

  private async hydrateCandidate(row: PersistedCandidate): Promise<CandidateRecord> {
    let because = row.because ?? "";
    if (this.artifacts && row.becauseArtifactId && row.becauseSha256) {
      try {
        because = new TextDecoder().decode(
          await this.artifacts.get({
            artifactId: row.becauseArtifactId,
            sha256: row.becauseSha256,
            policy: localOnlyPolicy(),
          }),
        );
      } catch {
        because = "unavailable";
      }
    }
    return {
      candidateId: row.candidateId,
      signature: row.signature,
      state: row.state,
      because,
      needed: row.needed,
      updatedAt: row.updatedAt,
    };
  }

  private async hydrateReview(row: PersistedReview): Promise<ReviewRecord> {
    let findings = row.findings ?? [];
    if (this.artifacts && row.findingsArtifactId && row.findingsSha256) {
      try {
        findings = await getJsonArtifact(this.artifacts, {
          artifactId: row.findingsArtifactId,
          sha256: row.findingsSha256,
          policy: localOnlyPolicy(),
        });
      } catch {
        findings = ["unavailable"];
      }
    }
    return {
      reviewId: row.reviewId,
      triggerCode: row.triggerCode,
      at: row.at,
      findings,
      sessionsAtReview: row.sessionsAtReview,
      episodesAtReview: row.episodesAtReview,
      candidatesAtReview: row.candidatesAtReview,
      builtReflexesAtReview: row.builtReflexesAtReview,
    };
  }

  private async voidOp(op: Record<string, unknown>): Promise<void> {
    await this.call(op);
  }
}
