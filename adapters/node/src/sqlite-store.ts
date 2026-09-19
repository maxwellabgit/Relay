import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";
import type {
  CaseKind,
  CaseOrigin,
  CasePhase,
  CaseRecord,
  CaseStatus,
  FeedItemSnapshot,
  JudgmentRecord,
  RelaySnapshot,
} from "@relay/contracts";
import type { EngineStore, PersistedSourceEvent, WorkItem, WorkItemType } from "@relay/engine";

const migrationPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../../../packages/storage-schema/migrations/001_core.sql",
);

export class SqliteEngineStore implements EngineStore {
  private readonly db: DatabaseSync;

  constructor(filename = ":memory:") {
    this.db = new DatabaseSync(filename);
    this.db.exec(readFileSync(migrationPath, "utf8"));
    this.db
      .prepare("INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, ?)")
      .run(new Date().toISOString());
  }

  close(): void {
    this.db.close();
  }

  async ensureSession(sessionId: string, createdAt: string): Promise<void> {
    this.db
      .prepare(`INSERT OR IGNORE INTO sessions(session_id, created_at, listening) VALUES (?, ?, 0)`)
      .run(sessionId, createdAt);
  }

  async setListening(sessionId: string, listening: boolean): Promise<void> {
    this.db
      .prepare(`UPDATE sessions SET listening = ? WHERE session_id = ?`)
      .run(listening ? 1 : 0, sessionId);
  }

  async getListening(sessionId: string): Promise<boolean> {
    const row = this.db
      .prepare(`SELECT listening FROM sessions WHERE session_id = ?`)
      .get(sessionId) as { listening: number } | undefined;
    return row?.listening === 1;
  }

  async persistFinalSource(event: PersistedSourceEvent): Promise<{ inserted: boolean }> {
    const result = this.db
      .prepare(
        `INSERT OR IGNORE INTO source_events(
          source_event_id, session_id, segment_id, revision, sequence, origin, speaker_key,
          start_ms, end_ms, final, text_artifact_id, text_sha256, policy_json, created_at
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?)`,
      )
      .run(
        event.sourceEventId,
        event.sessionId,
        event.segment.segmentId,
        event.segment.revision,
        event.segment.sequence,
        event.segment.origin,
        event.segment.speakerKey,
        event.segment.startMs,
        event.segment.endMs,
        event.textArtifactId,
        event.textSha256,
        JSON.stringify(event.policy),
        event.createdAt,
      );
    return { inserted: Number(result.changes) > 0 };
  }

  async createCase(input: {
    caseId: string;
    origin: CaseOrigin;
    kind: CaseKind;
    priority: number;
    parentCaseId?: string;
    at: string;
  }): Promise<CaseRecord> {
    this.db
      .prepare(
        `INSERT INTO cases(
          case_id, version, origin, kind, status, phase, priority, parent_case_id, created_at, updated_at
        ) VALUES (?, 1, ?, ?, 'active', 'intake', ?, ?, ?, ?)`,
      )
      .run(
        input.caseId,
        input.origin,
        input.kind,
        input.priority,
        input.parentCaseId ?? null,
        input.at,
        input.at,
      );
    await this.appendCaseEvent(input.caseId, 1, "case.created", input.at, {
      origin: input.origin,
      kind: input.kind,
    });
    const record = await this.getCase(input.caseId);
    if (!record) throw new Error("case_create_failed");
    return record;
  }

  async getCase(caseId: string): Promise<CaseRecord | null> {
    const row = this.db.prepare(`SELECT * FROM cases WHERE case_id = ?`).get(caseId) as
      | DbCase
      | undefined;
    return row ? mapCase(row) : null;
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
    const current = await this.getCase(caseId);
    if (!current || current.version !== expectedVersion) return null;
    const nextVersion = expectedVersion + 1;
    this.db
      .prepare(
        `UPDATE cases SET
          version = ?,
          status = COALESCE(?, status),
          phase = COALESCE(?, phase),
          wait_kind = ?,
          updated_at = ?
        WHERE case_id = ? AND version = ?`,
      )
      .run(
        nextVersion,
        patch.status ?? null,
        patch.phase ?? null,
        patch.waitKind === undefined ? (current.waitKind ?? null) : patch.waitKind,
        patch.at,
        caseId,
        expectedVersion,
      );
    return this.getCase(caseId);
  }

  async appendCaseEvent(
    caseId: string,
    caseVersion: number,
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO case_events(event_id, case_id, case_version, type, at, payload_json)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
      .run(
        `${caseId}:${caseVersion}:${type}:${at}`,
        caseId,
        caseVersion,
        type,
        at,
        JSON.stringify(payload),
      );
  }

  async listActiveCases(): Promise<readonly CaseRecord[]> {
    const rows = this.db
      .prepare(
        `SELECT * FROM cases WHERE status IN ('active','waiting') ORDER BY priority DESC, created_at`,
      )
      .all() as DbCase[];
    return rows.map(mapCase);
  }

  async listFeedItems(): Promise<readonly FeedItemSnapshot[]> {
    const rows = this.db
      .prepare(`SELECT payload_json FROM domain_events WHERE type = 'feed.item' ORDER BY sequence`)
      .all() as { payload_json: string }[];
    return rows.map((r) => JSON.parse(r.payload_json) as FeedItemSnapshot);
  }

  async addFeedItem(item: FeedItemSnapshot): Promise<void> {
    await this.appendDomainEvent(
      "feed.item",
      item.createdAt,
      item as unknown as Record<string, unknown>,
    );
  }

  async listSourceSegments(sessionId: string): Promise<RelaySnapshot["sourceSegments"]> {
    const rows = this.db
      .prepare(
        `SELECT segment_id, speaker_key, sequence, origin, final, text_artifact_id
         FROM source_events WHERE session_id = ? ORDER BY sequence`,
      )
      .all(sessionId) as {
      segment_id: string;
      speaker_key: string | null;
      sequence: number;
      origin: string;
      final: number;
      text_artifact_id: string;
    }[];
    return rows.map((r) => ({
      segmentId: r.segment_id,
      speakerKey: r.speaker_key,
      text: `[artifact:${r.text_artifact_id}]`,
      final: r.final === 1,
      origin: r.origin,
      sequence: r.sequence,
    }));
  }

  async enqueue(item: WorkItem): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
      .run(
        item.workId,
        item.type,
        item.priority,
        item.availableAt,
        JSON.stringify(item.payload),
        item.createdAt,
      );
  }

  async claimNext(now: string, owner: string, leaseMs: number): Promise<WorkItem | null> {
    const row = this.db
      .prepare(
        `SELECT * FROM work_items
         WHERE available_at <= ?
           AND (lease_until IS NULL OR lease_until < ?)
         ORDER BY priority DESC, created_at
         LIMIT 1`,
      )
      .get(now, now) as DbWork | undefined;
    if (!row) return null;
    const leaseUntil = new Date(Date.parse(now) + leaseMs).toISOString();
    const result = this.db
      .prepare(
        `UPDATE work_items SET lease_owner = ?, lease_until = ?
         WHERE work_id = ? AND (lease_until IS NULL OR lease_until < ?)`,
      )
      .run(owner, leaseUntil, row.work_id, now);
    if (Number(result.changes) === 0) return null;
    return {
      workId: row.work_id,
      type: row.type as WorkItemType,
      priority: row.priority,
      availableAt: row.available_at,
      payload: JSON.parse(row.payload_json) as Record<string, unknown>,
      createdAt: row.created_at,
    };
  }

  async complete(workId: string): Promise<void> {
    this.db.prepare(`DELETE FROM work_items WHERE work_id = ?`).run(workId);
  }

  async requeue(workId: string, availableAt: string): Promise<void> {
    this.db
      .prepare(
        `UPDATE work_items SET available_at = ?, lease_owner = NULL, lease_until = NULL WHERE work_id = ?`,
      )
      .run(availableAt, workId);
  }

  async upsertJudgment(record: JudgmentRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO judgments(
          judgment_id, provider, question_set_id, question_set_version, model, status,
          case_id, case_version, request_artifact_id, request_hash, response_artifact_id,
          response_hash, failure_category, input_tokens, output_tokens, elapsed_ms,
          created_at, completed_at
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(judgment_id) DO UPDATE SET
          status=excluded.status,
          response_artifact_id=excluded.response_artifact_id,
          response_hash=excluded.response_hash,
          failure_category=excluded.failure_category,
          input_tokens=excluded.input_tokens,
          output_tokens=excluded.output_tokens,
          elapsed_ms=excluded.elapsed_ms,
          completed_at=excluded.completed_at`,
      )
      .run(
        record.judgmentId,
        record.provider ?? null,
        record.questionSetId,
        record.questionSetVersion,
        record.model,
        record.status,
        record.caseId ?? null,
        record.caseVersion ?? null,
        record.requestArtifactId ?? null,
        record.requestHash ?? null,
        record.responseArtifactId ?? null,
        record.responseHash ?? null,
        record.failureCategory ?? null,
        record.inputTokens ?? null,
        record.outputTokens ?? null,
        record.elapsedMs ?? null,
        record.createdAt,
        record.completedAt ?? null,
      );
  }

  async findCompletedJudgmentByHash(requestHash: string): Promise<JudgmentRecord | null> {
    const row = this.db
      .prepare(`SELECT * FROM judgments WHERE request_hash = ? AND status = 'completed' LIMIT 1`)
      .get(requestHash) as DbJudgment | undefined;
    return row ? mapJudgment(row) : null;
  }

  async appendDomainEvent(
    type: string,
    at: string,
    payload: Record<string, unknown>,
  ): Promise<number> {
    const info = this.db
      .prepare(`INSERT INTO domain_events(type, at, payload_json) VALUES (?, ?, ?)`)
      .run(type, at, JSON.stringify(payload));
    return Number(info.lastInsertRowid);
  }

  async listDomainEvents(limit: number): Promise<
    readonly {
      sequence: number;
      type: string;
      at: string;
      payload: Record<string, unknown>;
    }[]
  > {
    const rows = this.db
      .prepare(
        `SELECT sequence, type, at, payload_json FROM domain_events ORDER BY sequence DESC LIMIT ?`,
      )
      .all(Math.max(0, limit)) as {
      sequence: number;
      type: string;
      at: string;
      payload_json: string;
    }[];
    return rows.reverse().map((row) => ({
      sequence: row.sequence,
      type: row.type,
      at: row.at,
      payload: JSON.parse(row.payload_json) as Record<string, unknown>,
    }));
  }

  async countWorkItems(): Promise<number> {
    const row = this.db.prepare(`SELECT COUNT(*) AS c FROM work_items`).get() as { c: number };
    return row.c;
  }
}

type DbCase = {
  case_id: string;
  version: number;
  origin: string;
  kind: string;
  status: string;
  phase: string;
  priority: number;
  parent_case_id: string | null;
  wait_kind: string | null;
  created_at: string;
  updated_at: string;
};

type DbWork = {
  work_id: string;
  type: string;
  priority: number;
  available_at: string;
  payload_json: string;
  created_at: string;
};

type DbJudgment = {
  judgment_id: string;
  provider: string | null;
  question_set_id: string;
  question_set_version: string;
  model: string;
  status: string;
  case_id: string | null;
  case_version: number | null;
  request_artifact_id: string | null;
  request_hash: string | null;
  response_artifact_id: string | null;
  response_hash: string | null;
  failure_category: string | null;
  input_tokens: number | null;
  output_tokens: number | null;
  elapsed_ms: number | null;
  created_at: string;
  completed_at: string | null;
};

function mapCase(row: DbCase): CaseRecord {
  return {
    caseId: row.case_id,
    version: row.version,
    origin: row.origin as CaseOrigin,
    kind: row.kind as CaseKind,
    status: row.status as CaseStatus,
    phase: row.phase as CasePhase,
    priority: row.priority,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    ...(row.parent_case_id ? { parentCaseId: row.parent_case_id } : {}),
    ...(row.wait_kind ? { waitKind: row.wait_kind } : {}),
  };
}

function mapJudgment(row: DbJudgment): JudgmentRecord {
  return {
    judgmentId: row.judgment_id,
    questionSetId: row.question_set_id,
    questionSetVersion: row.question_set_version,
    model: row.model,
    status: row.status as JudgmentRecord["status"],
    createdAt: row.created_at,
    ...(row.provider ? { provider: row.provider } : {}),
    ...(row.case_id ? { caseId: row.case_id } : {}),
    ...(row.case_version != null ? { caseVersion: row.case_version } : {}),
    ...(row.request_artifact_id ? { requestArtifactId: row.request_artifact_id } : {}),
    ...(row.request_hash ? { requestHash: row.request_hash } : {}),
    ...(row.response_artifact_id ? { responseArtifactId: row.response_artifact_id } : {}),
    ...(row.response_hash ? { responseHash: row.response_hash } : {}),
    ...(row.failure_category ? { failureCategory: row.failure_category } : {}),
    ...(row.input_tokens != null ? { inputTokens: row.input_tokens } : {}),
    ...(row.output_tokens != null ? { outputTokens: row.output_tokens } : {}),
    ...(row.elapsed_ms != null ? { elapsedMs: row.elapsed_ms } : {}),
    ...(row.completed_at ? { completedAt: row.completed_at } : {}),
  };
}
