import type { DatabaseSync } from "node:sqlite";
import type {
  CandidateRecord,
  EpisodeRecord,
  LearningStore,
  MemoryKind,
  MemoryRecord,
  PatternRecord,
  ReceiptRecord,
  ReviewRecord,
  WorkSessionRecord,
} from "@relay/engine";
import { RETENTION_MS } from "@relay/engine";

export class SqliteLearning implements LearningStore {
  constructor(private readonly db: DatabaseSync) {}

  async putMemory(record: MemoryRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO memories(memory_id, kind, key, value_json, source, created_at)
         VALUES (?, ?, ?, ?, ?, ?)
         ON CONFLICT(kind, key) DO UPDATE SET
           value_json=excluded.value_json, source=excluded.source`,
      )
      .run(record.memoryId, record.kind, record.key, JSON.stringify(record.value), record.source, record.createdAt);
  }

  async deleteMemory(kind: MemoryKind, key: string): Promise<void> {
    this.db.prepare(`DELETE FROM memories WHERE kind = ? AND key = ?`).run(kind, key);
  }

  async getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null> {
    const row = this.db
      .prepare(`SELECT * FROM memories WHERE kind = ? AND key = ?`)
      .get(kind, key) as MemoryRow | undefined;
    return row ? mapMemory(row) : null;
  }

  async listMemories(): Promise<readonly MemoryRecord[]> {
    return (this.db.prepare(`SELECT * FROM memories`).all() as MemoryRow[]).map(mapMemory);
  }

  async openSession(record: WorkSessionRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO work_sessions(session_id, started_at, ended_at, termination, episode_count)
         VALUES (?, ?, ?, ?, ?)`,
      )
      .run(record.sessionId, record.startedAt, record.endedAt, record.termination, record.episodeCount);
  }

  async closeSession(
    sessionId: string,
    endedAt: string,
    termination: "completed" | "abandoned",
    episodeCount: number,
  ): Promise<void> {
    this.db
      .prepare(`UPDATE work_sessions SET ended_at = ?, termination = ?, episode_count = ? WHERE session_id = ?`)
      .run(endedAt, termination, episodeCount, sessionId);
  }

  async currentSession(): Promise<WorkSessionRecord | null> {
    const row = this.db
      .prepare(`SELECT * FROM work_sessions WHERE termination = 'open' ORDER BY started_at DESC LIMIT 1`)
      .get() as SessionRow | undefined;
    return row ? mapSession(row) : null;
  }

  async listSessions(): Promise<readonly WorkSessionRecord[]> {
    return (this.db.prepare(`SELECT * FROM work_sessions`).all() as SessionRow[]).map(mapSession);
  }

  async putEpisode(record: EpisodeRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        record.episodeId,
        record.sessionId,
        record.caseId,
        record.signature,
        record.outcome,
        record.startedAt,
        record.completedAt,
      );
  }

  async listEpisodes(): Promise<readonly EpisodeRecord[]> {
    return (this.db.prepare(`SELECT * FROM work_episodes`).all() as EpisodeRow[]).map(mapEpisode);
  }

  async putReceipt(record: ReceiptRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT INTO decision_receipts(
          receipt_id, case_id, gate_id, policy_version, question_type, provider,
          probabilities_json, thresholds_json, selected_option, result, reason_code,
          latency_ms, retries, created_at,
          decision_id, judgment_id, reflex_id, selected_option_id, option_labels_json,
          requested_at, completed_at
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        record.receiptId,
        record.caseId,
        record.gateId,
        record.policyVersion,
        record.questionType,
        record.provider,
        JSON.stringify(record.probabilities),
        JSON.stringify(record.thresholds),
        record.selectedOption,
        record.result,
        record.reasonCode,
        record.latencyMs,
        record.retries,
        record.createdAt,
        record.decisionId,
        record.judgmentId,
        record.reflexId,
        record.selectedOptionId,
        JSON.stringify(record.optionLabels),
        record.requestedAt,
        record.completedAt,
      );
  }

  async listReceipts(): Promise<readonly ReceiptRecord[]> {
    return (this.db.prepare(`SELECT * FROM decision_receipts ORDER BY created_at`).all() as ReceiptRow[]).map(
      mapReceipt,
    );
  }

  async putPattern(record: PatternRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO pattern_evidence(
          signature, count, session_ids_json, outcomes_json, first_at, last_at, evidence_ids_json
        ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        record.signature,
        record.count,
        JSON.stringify(record.sessionIds),
        JSON.stringify(record.outcomes),
        record.firstAt,
        record.lastAt,
        JSON.stringify(record.evidenceIds),
      );
  }

  async getPattern(signature: string): Promise<PatternRecord | null> {
    const row = this.db.prepare(`SELECT * FROM pattern_evidence WHERE signature = ?`).get(signature) as
      | PatternRow
      | undefined;
    return row ? mapPattern(row) : null;
  }

  async listPatterns(): Promise<readonly PatternRecord[]> {
    return (this.db.prepare(`SELECT * FROM pattern_evidence`).all() as PatternRow[]).map(mapPattern);
  }

  async putCandidate(record: CandidateRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO expansion_candidates(candidate_id, signature, state, because, needed, updated_at)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
      .run(record.candidateId, record.signature, record.state, record.because, record.needed, record.updatedAt);
  }

  async listCandidates(): Promise<readonly CandidateRecord[]> {
    return (this.db.prepare(`SELECT * FROM expansion_candidates`).all() as CandidateRow[]).map(mapCandidate);
  }

  async putReview(record: ReviewRecord): Promise<void> {
    this.db
      .prepare(
        `INSERT OR IGNORE INTO review_runs(
          review_id, trigger_code, at, findings_json,
          sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        record.reviewId,
        record.triggerCode,
        record.at,
        JSON.stringify(record.findings),
        record.sessionsAtReview,
        record.episodesAtReview,
        record.candidatesAtReview,
        record.builtReflexesAtReview,
      );
    this.db
      .prepare(
        `INSERT INTO review_cursors(
          trigger_code, review_id, sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review
        ) VALUES (?, ?, ?, ?, ?, ?)
        ON CONFLICT(trigger_code) DO UPDATE SET
          review_id=excluded.review_id,
          sessions_at_review=excluded.sessions_at_review,
          episodes_at_review=excluded.episodes_at_review,
          candidates_at_review=excluded.candidates_at_review,
          built_reflexes_at_review=excluded.built_reflexes_at_review`,
      )
      .run(
        record.triggerCode,
        record.reviewId,
        record.sessionsAtReview,
        record.episodesAtReview,
        record.candidatesAtReview,
        record.builtReflexesAtReview,
      );
  }

  async listReviews(): Promise<readonly ReviewRecord[]> {
    return (this.db.prepare(`SELECT * FROM review_runs`).all() as ReviewRow[]).map(mapReview);
  }

  async compact(nowIso: string): Promise<number> {
    const cutoff = new Date(Date.parse(nowIso) - RETENTION_MS).toISOString();
    const result = this.db
      .prepare(
        `DELETE FROM work_episodes
         WHERE outcome IN ('abandoned', 'failed')
           AND completed_at IS NOT NULL
           AND completed_at < ?`,
      )
      .run(cutoff);
    this.db.prepare(`DELETE FROM dead_letters WHERE at < ?`).run(cutoff);
    return Number(result.changes);
  }
}

type MemoryRow = {
  memory_id: string;
  kind: MemoryKind;
  key: string;
  value_json: string;
  source: MemoryRecord["source"];
  created_at: string;
};
type SessionRow = {
  session_id: string;
  started_at: string;
  ended_at: string | null;
  termination: WorkSessionRecord["termination"];
  episode_count: number;
};
type EpisodeRow = {
  episode_id: string;
  session_id: string;
  case_id: string | null;
  signature: string;
  outcome: EpisodeRecord["outcome"];
  started_at: string;
  completed_at: string | null;
};
type ReceiptRow = {
  receipt_id: string;
  case_id: string | null;
  gate_id: string;
  policy_version: string;
  question_type: ReceiptRecord["questionType"];
  provider: string;
  probabilities_json: string;
  thresholds_json: string;
  selected_option: string | null;
  result: ReceiptRecord["result"];
  reason_code: string;
  latency_ms: number | null;
  retries: number;
  created_at: string;
  decision_id: string | null;
  judgment_id: string | null;
  reflex_id: string | null;
  selected_option_id: string | null;
  option_labels_json: string | null;
  requested_at: string | null;
  completed_at: string | null;
};
type PatternRow = {
  signature: string;
  count: number;
  session_ids_json: string;
  outcomes_json: string;
  first_at: string;
  last_at: string;
  evidence_ids_json: string;
};
type CandidateRow = {
  candidate_id: string;
  signature: string;
  state: CandidateRecord["state"];
  because: string;
  needed: string;
  updated_at: string;
};
type ReviewRow = {
  review_id: string;
  trigger_code: string;
  at: string;
  findings_json: string;
  sessions_at_review: number;
  episodes_at_review: number;
  candidates_at_review: number;
  built_reflexes_at_review: number;
};

function mapMemory(row: MemoryRow): MemoryRecord {
  return {
    memoryId: row.memory_id,
    kind: row.kind,
    key: row.key,
    value: JSON.parse(row.value_json) as Record<string, string>,
    source: row.source,
    createdAt: row.created_at,
  };
}
function mapSession(row: SessionRow): WorkSessionRecord {
  return {
    sessionId: row.session_id,
    startedAt: row.started_at,
    endedAt: row.ended_at,
    termination: row.termination,
    episodeCount: row.episode_count,
  };
}
function mapEpisode(row: EpisodeRow): EpisodeRecord {
  return {
    episodeId: row.episode_id,
    sessionId: row.session_id,
    caseId: row.case_id,
    signature: row.signature,
    outcome: row.outcome,
    startedAt: row.started_at,
    completedAt: row.completed_at,
  };
}
function mapReceipt(row: ReceiptRow): ReceiptRecord {
  return {
    receiptId: row.receipt_id,
    decisionId: row.decision_id ?? row.receipt_id,
    caseId: row.case_id,
    judgmentId: row.judgment_id,
    reflexId: row.reflex_id,
    gateId: row.gate_id,
    policyVersion: row.policy_version,
    questionType: row.question_type,
    provider: row.provider,
    probabilities: JSON.parse(row.probabilities_json) as Record<string, number>,
    thresholds: JSON.parse(row.thresholds_json) as Record<string, number>,
    optionLabels: JSON.parse(row.option_labels_json ?? "{}") as Record<string, string>,
    selectedOption: row.selected_option,
    selectedOptionId: row.selected_option_id ?? row.selected_option,
    result: row.result,
    reasonCode: row.reason_code,
    latencyMs: row.latency_ms,
    retries: row.retries,
    requestedAt: row.requested_at,
    completedAt: row.completed_at,
    createdAt: row.created_at,
  };
}
function mapPattern(row: PatternRow): PatternRecord {
  return {
    signature: row.signature,
    count: row.count,
    sessionIds: JSON.parse(row.session_ids_json) as string[],
    outcomes: JSON.parse(row.outcomes_json) as Record<string, number>,
    firstAt: row.first_at,
    lastAt: row.last_at,
    evidenceIds: JSON.parse(row.evidence_ids_json) as string[],
  };
}
function mapCandidate(row: CandidateRow): CandidateRecord {
  return {
    candidateId: row.candidate_id,
    signature: row.signature,
    state: row.state,
    because: row.because,
    needed: row.needed,
    updatedAt: row.updated_at,
  };
}
function mapReview(row: ReviewRow): ReviewRecord {
  return {
    reviewId: row.review_id,
    triggerCode: row.trigger_code,
    at: row.at,
    findings: JSON.parse(row.findings_json) as string[],
    sessionsAtReview: row.sessions_at_review,
    episodesAtReview: row.episodes_at_review,
    candidatesAtReview: row.candidates_at_review,
    builtReflexesAtReview: row.built_reflexes_at_review,
  };
}
