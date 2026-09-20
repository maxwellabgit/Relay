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
import type { EngineStore, PersistedSourceEvent, WorkItem, WorkItemType } from "@relay/engine";
import { InMemoryLearning } from "@relay/engine";

type SessionRow = {
  createdAt: string;
  listening: boolean;
};

type SourceRow = PersistedSourceEvent & { final: true };

type CaseEventRow = {
  eventId: string;
  caseId: string;
  caseVersion: number;
  type: string;
  at: string;
  payload: Record<string, unknown>;
};

type DomainEventRow = {
  sequence: number;
  type: string;
  at: string;
  payload: Record<string, unknown>;
};

type WorkRow = WorkItem & {
  leaseOwner: string | null;
  leaseUntil: string | null;
};

export class MemoryEngineStore implements EngineStore {
  readonly learning = new InMemoryLearning();
  private readonly sessions = new Map<string, SessionRow>();
  private readonly sourceEvents = new Map<string, SourceRow>();
  private readonly cases = new Map<string, CaseRecord>();
  private readonly caseEvents: CaseEventRow[] = [];
  private readonly domainEvents: DomainEventRow[] = [];
  private readonly feedItems: FeedItemRecord[] = [];
  private readonly workItems = new Map<string, WorkRow>();
  private readonly judgments = new Map<string, JudgmentRecord>();
  private readonly deadLetters: { workId: string; reasonCode: string; at: string }[] = [];
  private readonly judgmentAttempts = new Map<string, unknown>();
  private domainSeq = 0;

  close(): void {
    this.sessions.clear();
    this.sourceEvents.clear();
    this.cases.clear();
    this.caseEvents.length = 0;
    this.domainEvents.length = 0;
    this.workItems.clear();
    this.judgments.clear();
  }

  async ensureSession(sessionId: string, createdAt: string): Promise<void> {
    if (!this.sessions.has(sessionId)) {
      this.sessions.set(sessionId, { createdAt, listening: false });
    }
  }

  async setListening(sessionId: string, listening: boolean): Promise<void> {
    const session = this.sessions.get(sessionId);
    if (!session) return;
    this.sessions.set(sessionId, { ...session, listening });
  }

  async getListening(sessionId: string): Promise<boolean> {
    return this.sessions.get(sessionId)?.listening === true;
  }

  async persistFinalSource(event: PersistedSourceEvent): Promise<{ inserted: boolean }> {
    if (this.sourceEvents.has(event.sourceEventId)) {
      return { inserted: false };
    }
    this.sourceEvents.set(event.sourceEventId, { ...event, final: true });
    return { inserted: true };
  }

  async createCase(input: {
    caseId: string;
    origin: CaseOrigin;
    kind: CaseKind;
    priority: number;
    parentCaseId?: string;
    at: string;
  }): Promise<CaseRecord> {
    const record: CaseRecord = {
      caseId: input.caseId,
      version: 1,
      origin: input.origin,
      kind: input.kind,
      status: "active",
      phase: "intake",
      priority: input.priority,
      createdAt: input.at,
      updatedAt: input.at,
      ...(input.parentCaseId ? { parentCaseId: input.parentCaseId } : {}),
    };
    this.cases.set(input.caseId, record);
    await this.appendCaseEvent(input.caseId, 1, "case.created", input.at, {
      origin: input.origin,
      kind: input.kind,
    });
    return record;
  }

  async getCase(caseId: string): Promise<CaseRecord | null> {
    return this.cases.get(caseId) ?? null;
  }

  async updateCase(
    caseId: string,
    expectedVersion: number,
    patch: {
      status?: CaseStatus;
      phase?: CasePhase;
      waitKind?: string | null;
      at: string;
    },
  ): Promise<CaseRecord | null> {
    const current = this.cases.get(caseId);
    if (!current || current.version !== expectedVersion) return null;

    const waitKind =
      patch.waitKind === undefined
        ? current.waitKind
        : patch.waitKind === null
          ? undefined
          : patch.waitKind;

    const next: CaseRecord = {
      caseId: current.caseId,
      version: expectedVersion + 1,
      origin: current.origin,
      kind: current.kind,
      status: patch.status ?? current.status,
      phase: patch.phase ?? current.phase,
      priority: current.priority,
      createdAt: current.createdAt,
      updatedAt: patch.at,
      ...(current.parentCaseId ? { parentCaseId: current.parentCaseId } : {}),
      ...(waitKind !== undefined ? { waitKind } : {}),
    };

    this.cases.set(caseId, next);
    return next;
  }

  async appendCaseEvent(
    caseId: string,
    caseVersion: number,
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<void> {
    this.caseEvents.push({
      eventId: `${caseId}:${caseVersion}:${type}:${at}`,
      caseId,
      caseVersion,
      type,
      at,
      payload,
    });
  }

  async listActiveCases(): Promise<readonly CaseRecord[]> {
    return [...this.cases.values()]
      .filter((c) => c.status === "active" || c.status === "waiting" || c.status === "blocked" || c.status === "failed")
      .sort((a, b) => b.priority - a.priority || a.createdAt.localeCompare(b.createdAt));
  }

  async listFeedItemRecords(): Promise<readonly FeedItemRecord[]> {
    return [...this.feedItems].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  }

  async addFeedItem(item: FeedItemRecord): Promise<void> {
    this.feedItems.push(item);
  }

  async listSourceSegments(sessionId: string): Promise<RelaySnapshot["sourceSegments"]> {
    return [...this.sourceEvents.values()]
      .filter((e) => e.sessionId === sessionId)
      .sort((a, b) => a.segment.sequence - b.segment.sequence)
      .map((e) => ({
        segmentId: e.segment.segmentId,
        speakerKey: e.segment.speakerKey,
        text: e.segment.text,
        final: true,
        origin: e.segment.origin,
        sequence: e.segment.sequence,
      }));
  }

  async enqueue(item: WorkItem): Promise<void> {
    this.workItems.set(item.workId, {
      ...item,
      leaseOwner: null,
      leaseUntil: null,
    });
  }

  async claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null> {
    const candidates = [...this.workItems.values()]
      .filter(
        (w) =>
          w.availableAt <= now && (w.leaseUntil === null || w.leaseUntil < now),
      )
      .sort((a, b) => b.priority - a.priority || a.createdAt.localeCompare(b.createdAt));

    const row = candidates[0];
    if (!row) return null;

    const leaseUntil = new Date(Date.parse(now) + leaseMs).toISOString();
    const current = this.workItems.get(row.workId);
    if (!current) return null;
    if (current.leaseUntil !== null && current.leaseUntil >= now) return null;

    this.workItems.set(row.workId, {
      ...current,
      leaseOwner: owner,
      leaseUntil,
    });

    return {
      workId: current.workId,
      type: current.type as WorkItemType,
      priority: current.priority,
      availableAt: current.availableAt,
      payload: current.payload,
      createdAt: current.createdAt,
    };
  }

  async complete(workId: string): Promise<void> {
    this.workItems.delete(workId);
  }

  async requeue(workId: string, availableAt: string, payload?: Record<string, unknown>): Promise<void> {
    const current = this.workItems.get(workId);
    if (!current) return;
    this.workItems.set(workId, {
      ...current,
      availableAt,
      ...(payload ? { payload } : {}),
      leaseOwner: null,
      leaseUntil: null,
    });
  }

  async upsertJudgment(record: JudgmentRecord): Promise<void> {
    const existing = this.judgments.get(record.judgmentId);
    if (!existing) {
      this.judgments.set(record.judgmentId, record);
      return;
    }
    this.judgments.set(record.judgmentId, {
      ...existing,
      status: record.status,
      ...(record.responseArtifactId !== undefined
        ? { responseArtifactId: record.responseArtifactId }
        : {}),
      ...(record.responseHash !== undefined ? { responseHash: record.responseHash } : {}),
      ...(record.failureCategory !== undefined ? { failureCategory: record.failureCategory } : {}),
      ...(record.inputTokens !== undefined ? { inputTokens: record.inputTokens } : {}),
      ...(record.outputTokens !== undefined ? { outputTokens: record.outputTokens } : {}),
      ...(record.elapsedMs !== undefined ? { elapsedMs: record.elapsedMs } : {}),
      ...(record.completedAt !== undefined ? { completedAt: record.completedAt } : {}),
    });
  }

  async findCompletedJudgmentByHash(requestHash: string): Promise<JudgmentRecord | null> {
    for (const record of this.judgments.values()) {
      if (record.requestHash === requestHash && record.status === "completed") {
        return record;
      }
    }
    return null;
  }

  async appendDomainEvent(
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<number> {
    const sequence = ++this.domainSeq;
    this.domainEvents.push({ sequence, type, at, payload });
    return sequence;
  }

  async listDomainEvents(limit: number): Promise<
    readonly {
      sequence: number;
      type: string;
      at: string;
      payload: Record<string, unknown>;
    }[]
  > {
    const take = Math.max(0, limit);
    return this.domainEvents.slice(-take);
  }

  async countWorkItems(): Promise<number> {
    return this.workItems.size;
  }

  async deadLetter(workId: string, reasonCode: string, at: string): Promise<void> {
    this.workItems.delete(workId);
    this.deadLetters.push({ workId, reasonCode, at });
  }

  async listDeadLetters(): Promise<readonly { workId: string; reasonCode: string; at: string }[]> {
    return this.deadLetters;
  }

  async upsertJudgmentAttempt(record: {
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
    this.judgmentAttempts.set(record.attemptId, record);
  }
}
