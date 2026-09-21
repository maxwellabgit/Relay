import type { DatabaseSync } from "node:sqlite";
import type { ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
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
import {
  foldPattern,
  getJsonArtifact,
  labelsOrUnavailable,
  packMemoryValue,
  putJsonArtifact,
  RETENTION_MS,
  unpackMemoryValue,
} from "@relay/engine";

export class SqliteLearning implements LearningStore {
  constructor(
    private readonly db: DatabaseSync,
    private readonly artifacts?: ArtifactStorePort,
  ) {}

  async putMemory(record: MemoryRecord & {
    contentArtifactId?: string | null;
    contentSha256?: string | null;
    metadata?: Readonly<Record<string, string>>;
  }): Promise<void> {
    let artifactId = record.contentArtifactId ?? null;
    let sha256 = record.contentSha256 ?? null;
    let metadata = record.metadata ?? {};
    if (!artifactId || !sha256) {
      const packed = packMemoryValue(record.kind, record.value);
      const ref = await this.requireArtifacts().put(
        new TextEncoder().encode(JSON.stringify(packed.prose)),
        localOnlyPolicy(),
      );
      artifactId = ref.artifactId;
      sha256 = ref.sha256;
      metadata = packed.metadata;
    }
    this.db
      .prepare(
        `INSERT INTO memories(
           memory_id, kind, key, value_json, source, created_at,
           content_artifact_id, content_sha256, metadata_json
         ) VALUES (?, ?, ?, '{}', ?, ?, ?, ?, ?)
         ON CONFLICT(kind, key) DO UPDATE SET
           memory_id=excluded.memory_id,
           value_json='{}',
           source=excluded.source,
           content_artifact_id=excluded.content_artifact_id,
           content_sha256=excluded.content_sha256,
           metadata_json=excluded.metadata_json`,
      )
      .run(
        record.memoryId,
        record.kind,
        record.key,
        record.source,
        record.createdAt,
        artifactId,
        sha256,
        JSON.stringify(metadata),
      );
  }

  async deleteMemory(kind: MemoryKind, key: string): Promise<void> {
    this.db.prepare(`DELETE FROM memories WHERE kind = ? AND key = ?`).run(kind, key);
  }

  async getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null> {
    const row = this.db
      .prepare(`SELECT * FROM memories WHERE kind = ? AND key = ?`)
      .get(kind, key) as MemoryRow | undefined;
    return row ? this.mapMemory(row) : null;
  }

  async listMemories(): Promise<readonly MemoryRecord[]> {
    const rows = this.db.prepare(`SELECT * FROM memories`).all() as MemoryRow[];
    return Promise.all(rows.map((row) => this.mapMemory(row)));
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
        `INSERT OR IGNORE INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
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

  async recordCompletedEpisode(record: EpisodeRecord): Promise<PatternRecord | null> {
    this.db.exec("BEGIN IMMEDIATE");
    try {
      this.db
        .prepare(
          `INSERT OR IGNORE INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
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
      if (record.outcome !== "completed") {
        this.db.exec("COMMIT");
        return null;
      }
      const existingRow = this.db
        .prepare(`SELECT * FROM pattern_evidence WHERE signature = ?`)
        .get(record.signature) as PatternRow | undefined;
      const existing = existingRow ? mapPattern(existingRow) : null;
      const pattern = foldPattern(existing, record);
      this.db
        .prepare(
          `INSERT OR REPLACE INTO pattern_evidence(
            signature, count, session_ids_json, outcomes_json, first_at, last_at, evidence_ids_json
          ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
        )
        .run(
          pattern.signature,
          pattern.count,
          JSON.stringify(pattern.sessionIds),
          JSON.stringify(pattern.outcomes),
          pattern.firstAt,
          pattern.lastAt,
          JSON.stringify(pattern.evidenceIds),
        );
      this.db.exec("COMMIT");
      return pattern;
    } catch (error) {
      this.db.exec("ROLLBACK");
      throw error;
    }
  }

  async putReceipt(record: ReceiptRecord & {
    labelsArtifactId?: string | null;
    labelsSha256?: string | null;
  }): Promise<void> {
    let labelsArtifactId = record.labelsArtifactId ?? null;
    let labelsSha256 = record.labelsSha256 ?? null;
    const labels = record.optionLabels ?? {};
    if ((!labelsArtifactId || !labelsSha256) && Object.keys(labels).length > 0) {
      const ref = await putJsonArtifact(this.requireArtifacts(), labels);
      labelsArtifactId = ref.artifactId;
      labelsSha256 = ref.sha256;
    }
    const selectedOptionId = record.selectedOptionId ?? record.selectedOption;
    this.db
      .prepare(
        `INSERT INTO decision_receipts(
          receipt_id, case_id, gate_id, policy_version, question_type, provider,
          probabilities_json, thresholds_json, selected_option, result, reason_code,
          latency_ms, retries, created_at,
          decision_id, judgment_id, reflex_id, selected_option_id, option_labels_json,
          requested_at, completed_at, labels_artifact_id, labels_sha256
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, '{}', ?, ?, ?, ?)`,
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
        selectedOptionId,
        record.result,
        record.reasonCode,
        record.latencyMs,
        record.retries,
        record.createdAt,
        record.decisionId,
        record.judgmentId,
        record.reflexId,
        selectedOptionId,
        record.requestedAt,
        record.completedAt,
        labelsArtifactId,
        labelsSha256,
      );
  }

  async listReceipts(): Promise<readonly ReceiptRecord[]> {
    const rows = this.db.prepare(`SELECT * FROM decision_receipts ORDER BY created_at`).all() as ReceiptRow[];
    return Promise.all(rows.map((row) => this.mapReceipt(row)));
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

  async putCandidate(record: CandidateRecord & {
    becauseArtifactId?: string | null;
    becauseSha256?: string | null;
  }): Promise<void> {
    let becauseArtifactId = record.becauseArtifactId ?? null;
    let becauseSha256 = record.becauseSha256 ?? null;
    if ((!becauseArtifactId || !becauseSha256) && record.because) {
      const ref = await this.requireArtifacts().put(
        new TextEncoder().encode(record.because),
        localOnlyPolicy(),
      );
      becauseArtifactId = ref.artifactId;
      becauseSha256 = ref.sha256;
    }
    this.db
      .prepare(
        `INSERT OR REPLACE INTO expansion_candidates(
           candidate_id, signature, state, because, needed, updated_at,
           because_artifact_id, because_sha256
         ) VALUES (?, ?, ?, '', ?, ?, ?, ?)`,
      )
      .run(
        record.candidateId,
        record.signature,
        record.state,
        record.needed,
        record.updatedAt,
        becauseArtifactId,
        becauseSha256,
      );
  }

  async listCandidates(): Promise<readonly CandidateRecord[]> {
    const rows = this.db.prepare(`SELECT * FROM expansion_candidates`).all() as CandidateRow[];
    return Promise.all(rows.map((row) => this.mapCandidate(row)));
  }

  async putReview(record: ReviewRecord & {
    findingsArtifactId?: string | null;
    findingsSha256?: string | null;
  }): Promise<void> {
    let findingsArtifactId = record.findingsArtifactId ?? null;
    let findingsSha256 = record.findingsSha256 ?? null;
    if ((!findingsArtifactId || !findingsSha256) && record.findings.length > 0) {
      const ref = await putJsonArtifact(this.requireArtifacts(), record.findings);
      findingsArtifactId = ref.artifactId;
      findingsSha256 = ref.sha256;
    }
    this.db
      .prepare(
        `INSERT OR IGNORE INTO review_runs(
          review_id, trigger_code, at, findings_json,
          sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review,
          findings_artifact_id, findings_sha256
        ) VALUES (?, ?, ?, '[]', ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        record.reviewId,
        record.triggerCode,
        record.at,
        record.sessionsAtReview,
        record.episodesAtReview,
        record.candidatesAtReview,
        record.builtReflexesAtReview,
        findingsArtifactId,
        findingsSha256,
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
    const rows = this.db.prepare(`SELECT * FROM review_runs`).all() as ReviewRow[];
    return Promise.all(rows.map((row) => this.mapReview(row)));
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

  private requireArtifacts(): ArtifactStorePort {
    if (!this.artifacts) throw new Error("artifact_store_required");
    return this.artifacts;
  }

  private async mapMemory(row: MemoryRow): Promise<MemoryRecord> {
    const metadata = JSON.parse(row.metadata_json ?? "{}") as Record<string, string>;
    let prose: Record<string, string> = {};
    if (row.content_artifact_id && row.content_sha256 && this.artifacts) {
      try {
        const bytes = await this.artifacts.get({
          artifactId: row.content_artifact_id,
          sha256: row.content_sha256,
          policy: localOnlyPolicy(),
        });
        prose = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, string>;
      } catch {
        prose = {};
      }
    }
    return {
      memoryId: row.memory_id,
      kind: row.kind,
      key: row.key,
      value: unpackMemoryValue(row.kind, prose, metadata),
      source: row.source,
      createdAt: row.created_at,
    };
  }

  private async mapReceipt(row: ReceiptRow): Promise<ReceiptRecord> {
    const probabilities = JSON.parse(row.probabilities_json) as Record<string, number>;
    const optionIds = Object.keys(probabilities);
    let optionLabels: Record<string, string> = {};
    if (row.labels_artifact_id && row.labels_sha256 && this.artifacts) {
      try {
        optionLabels = await getJsonArtifact(this.artifacts, {
          artifactId: row.labels_artifact_id,
          sha256: row.labels_sha256,
          policy: localOnlyPolicy(),
        });
      } catch {
        optionLabels = labelsOrUnavailable(optionIds.length > 0 ? optionIds : ["unavailable"], null);
      }
    } else if (optionIds.length > 0) {
      optionLabels = labelsOrUnavailable(optionIds, null);
    }
    const selectedOptionId = row.selected_option_id ?? row.selected_option;
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
      probabilities,
      thresholds: JSON.parse(row.thresholds_json) as Record<string, number>,
      optionLabels,
      selectedOption: selectedOptionId,
      selectedOptionId,
      result: row.result,
      reasonCode: row.reason_code,
      latencyMs: row.latency_ms,
      retries: row.retries,
      requestedAt: row.requested_at,
      completedAt: row.completed_at,
      createdAt: row.created_at,
    };
  }

  private async mapCandidate(row: CandidateRow): Promise<CandidateRecord> {
    let because = "";
    if (row.because_artifact_id && row.because_sha256 && this.artifacts) {
      try {
        const bytes = await this.artifacts.get({
          artifactId: row.because_artifact_id,
          sha256: row.because_sha256,
          policy: localOnlyPolicy(),
        });
        because = new TextDecoder().decode(bytes);
      } catch {
        because = "unavailable";
      }
    }
    return {
      candidateId: row.candidate_id,
      signature: row.signature,
      state: row.state,
      because,
      needed: row.needed,
      updatedAt: row.updated_at,
    };
  }

  private async mapReview(row: ReviewRow): Promise<ReviewRecord> {
    let findings: string[] = [];
    if (row.findings_artifact_id && row.findings_sha256 && this.artifacts) {
      try {
        findings = await getJsonArtifact(this.artifacts, {
          artifactId: row.findings_artifact_id,
          sha256: row.findings_sha256,
          policy: localOnlyPolicy(),
        });
      } catch {
        findings = ["unavailable"];
      }
    }
    return {
      reviewId: row.review_id,
      triggerCode: row.trigger_code,
      at: row.at,
      findings,
      sessionsAtReview: row.sessions_at_review,
      episodesAtReview: row.episodes_at_review,
      candidatesAtReview: row.candidates_at_review,
      builtReflexesAtReview: row.built_reflexes_at_review,
    };
  }
}

type MemoryRow = {
  memory_id: string;
  kind: MemoryKind;
  key: string;
  value_json: string;
  source: MemoryRecord["source"];
  created_at: string;
  content_artifact_id: string | null;
  content_sha256: string | null;
  metadata_json: string | null;
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
  labels_artifact_id: string | null;
  labels_sha256: string | null;
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
  because_artifact_id: string | null;
  because_sha256: string | null;
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
  findings_artifact_id: string | null;
  findings_sha256: string | null;
};

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
