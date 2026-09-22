export const REVIEW_SESSION_TRIGGER = 12;
export const REVIEW_REFLEX_TRIGGER = 4;
export const REVIEW_EPISODE_TRIGGER = 25;
export const REVIEW_CANDIDATE_TRIGGER = 3;
export const PATTERN_EPISODE_MINIMUM = 3;
export const PATTERN_SESSION_MINIMUM = 2;
export const BENEFIT_YES_MINIMUM = 0.7;
export const RETENTION_MS = 7 * 24 * 60 * 60 * 1000;
export const TRACE_RETENTION_MS = 30 * 24 * 60 * 60 * 1000;
export const TRACE_ROTATE_BYTES = 100_000_000;
export const RETENTION_LABEL = "trace 30d/100MB · failed 7d · memories until delete";

export type MemoryKind = "glossary" | "birthday" | "note" | "fact" | "recommendation";

export type MemoryRecord = {
  readonly memoryId: string;
  readonly kind: MemoryKind;
  readonly key: string;
  readonly value: Readonly<Record<string, string>>;
  readonly source: "explicit_user" | "pending";
  readonly createdAt: string;
};

export type WorkSessionRecord = {
  readonly sessionId: string;
  readonly startedAt: string;
  readonly endedAt: string | null;
  readonly termination: "open" | "completed" | "abandoned";
  readonly episodeCount: number;
};

export type EpisodeOutcome = "completed" | "unresolved" | "suggested" | "rejected" | "failed" | "abandoned";

export type EpisodeRecord = {
  readonly episodeId: string;
  readonly sessionId: string;
  readonly caseId: string | null;
  readonly signature: string;
  readonly outcome: EpisodeOutcome;
  readonly startedAt: string;
  readonly completedAt: string | null;
};

export type ReceiptRecord = {
  readonly receiptId: string;
  readonly decisionId: string;
  readonly caseId: string | null;
  readonly judgmentId: string | null;
  readonly reflexId: string | null;
  readonly gateId: string;
  readonly policyVersion: string;
  readonly questionType: "deterministic" | "choice" | "noul" | "user" | "not_applicable";
  readonly provider: string;
  readonly probabilities: Readonly<Record<string, number>>;
  readonly thresholds: Readonly<Record<string, number>>;
  readonly optionLabels: Readonly<Record<string, string>>;
  readonly selectedOption: string | null;
  readonly selectedOptionId: string | null;
  readonly result: "pass" | "fail" | "wait" | "not_applicable" | "blocked";
  readonly reasonCode: string;
  readonly latencyMs: number | null;
  readonly retries: number;
  readonly requestedAt: string | null;
  readonly completedAt: string | null;
  readonly createdAt: string;
};

export type PatternRecord = {
  readonly signature: string;
  readonly count: number;
  readonly sessionIds: readonly string[];
  readonly outcomes: Readonly<Record<string, number>>;
  readonly firstAt: string;
  readonly lastAt: string;
  readonly evidenceIds: readonly string[];
};

export type CandidateState =
  | "observing"
  | "candidate"
  | "qualified"
  | "proposed"
  | "approved"
  | "approved_for_build"
  | "rejected"
  | "snoozed"
  | "built"
  | "shadow"
  | "activation_ready"
  | "active"
  | "paused"
  | "retired"
  | "rolled_back";

export type PatternEvidenceUserAction =
  | "accepted"
  | "edited"
  | "rejected"
  | "ignored"
  | "undid"
  | "completed";

export type PatternEvidenceRecord = {
  readonly evidenceId: string;
  readonly signature: string;
  /** Source class only — never raw prose. */
  readonly sourceClass: "direct_ask" | "observed" | "verified_work" | "ambient" | "tool";
  readonly routeOrTool: string | null;
  readonly userAction: PatternEvidenceUserAction;
  readonly outcomeClass: string;
  readonly duplicateCount: number;
  readonly timeToActionMs: number | null;
  readonly feedback: string | null;
  readonly caseId: string | null;
  readonly createdAt: string;
};

export type CandidateBuildMeta = {
  readonly reflexId: string;
  readonly reflexVersion: number;
  readonly templateId: string;
  readonly shadowPass: boolean;
  readonly shadowReportJson: string;
  readonly priorActivation?: "inactive" | "active" | "paused";
};

export type CandidateRecord = {
  readonly candidateId: string;
  readonly signature: string;
  readonly state: CandidateState;
  readonly because: string;
  readonly needed: string;
  readonly updatedAt: string;
  readonly meta?: CandidateBuildMeta;
};

export type ReviewRecord = {
  readonly reviewId: string;
  readonly triggerCode: string;
  readonly at: string;
  readonly findings: readonly string[];
  readonly sessionsAtReview: number;
  readonly episodesAtReview: number;
  readonly candidatesAtReview: number;
  readonly builtReflexesAtReview: number;
};

export type LearningStore = {
  putMemory(record: MemoryRecord): Promise<void>;
  deleteMemory(kind: MemoryKind, key: string): Promise<void>;
  getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null>;
  listMemories(): Promise<readonly MemoryRecord[]>;
  openSession(record: WorkSessionRecord): Promise<void>;
  closeSession(
    sessionId: string,
    endedAt: string,
    termination: "completed" | "abandoned",
    episodeCount: number,
  ): Promise<void>;
  currentSession(): Promise<WorkSessionRecord | null>;
  listSessions(): Promise<readonly WorkSessionRecord[]>;
  putEpisode(record: EpisodeRecord): Promise<void>;
  listEpisodes(): Promise<readonly EpisodeRecord[]>;
  /** Atomically insert episode and fold pattern when outcome is completed. */
  recordCompletedEpisode(record: EpisodeRecord): Promise<PatternRecord | null>;
  putReceipt(record: ReceiptRecord): Promise<void>;
  listReceipts(): Promise<readonly ReceiptRecord[]>;
  putPattern(record: PatternRecord): Promise<void>;
  getPattern(signature: string): Promise<PatternRecord | null>;
  listPatterns(): Promise<readonly PatternRecord[]>;
  putCandidate(record: CandidateRecord): Promise<void>;
  listCandidates(): Promise<readonly CandidateRecord[]>;
  putPatternEvidence(record: PatternEvidenceRecord): Promise<void>;
  listPatternEvidence(signature?: string): Promise<readonly PatternEvidenceRecord[]>;
  putReview(record: ReviewRecord): Promise<void>;
  listReviews(): Promise<readonly ReviewRecord[]>;
  compact(nowIso: string): Promise<number>;
};

export function workSignature(kind: string, fields: Readonly<Record<string, string>>): string | null {
  if (!/^[a-z][a-z0-9.]{0,31}$/.test(kind)) return null;
  const keys = Object.keys(fields).sort();
  for (const key of keys) {
    const value = fields[key];
    if (!/^[a-z0-9_]{1,24}$/.test(key)) return null;
    if (typeof value !== "string" || !/^[A-Za-z0-9._+-]{1,32}$/.test(value)) return null;
  }
  return [kind, ...keys.map((key) => `${key}=${fields[key]}`)].join("|");
}

export function foldPattern(existing: PatternRecord | null, episode: EpisodeRecord): PatternRecord {
  if (existing?.evidenceIds.includes(episode.episodeId)) {
    return existing;
  }
  const successful = episode.outcome === "completed";
  const sessions = new Set(existing?.sessionIds ?? []);
  if (successful) sessions.add(episode.sessionId);
  const outcomes = { ...(existing?.outcomes ?? {}) };
  outcomes[episode.outcome] = (outcomes[episode.outcome] ?? 0) + 1;
  return {
    signature: episode.signature,
    count: (existing?.count ?? 0) + (successful ? 1 : 0),
    sessionIds: [...sessions],
    outcomes,
    firstAt: existing?.firstAt ?? episode.completedAt ?? episode.startedAt,
    lastAt: episode.completedAt ?? episode.startedAt,
    evidenceIds: successful ? [...(existing?.evidenceIds ?? []), episode.episodeId] : [...(existing?.evidenceIds ?? [])],
  };
}

export function patternReady(pattern: PatternRecord): boolean {
  return pattern.count >= PATTERN_EPISODE_MINIMUM && pattern.sessionIds.length >= PATTERN_SESSION_MINIMUM;
}

export function reviewTrigger(input: {
  readonly completeSessions: number;
  readonly builtReflexes: number;
  readonly completeEpisodes: number;
  readonly qualifiedCandidates: number;
}): string | null {
  if (input.completeSessions >= REVIEW_SESSION_TRIGGER) return "sessions";
  if (input.builtReflexes >= REVIEW_REFLEX_TRIGGER) return "reflexes";
  if (input.completeEpisodes >= REVIEW_EPISODE_TRIGGER) return "episodes";
  if (input.qualifiedCandidates >= REVIEW_CANDIDATE_TRIGGER) return "candidates";
  return null;
}

export class InMemoryLearning implements LearningStore {
  private readonly memories = new Map<string, MemoryRecord>();
  private readonly sessions = new Map<string, WorkSessionRecord>();
  private episodes: EpisodeRecord[] = [];
  private readonly receipts: ReceiptRecord[] = [];
  private readonly patterns = new Map<string, PatternRecord>();
  private readonly candidates = new Map<string, CandidateRecord>();
  private readonly evidenceEvents: PatternEvidenceRecord[] = [];
  private readonly reviews: ReviewRecord[] = [];

  async putMemory(record: MemoryRecord): Promise<void> {
    this.memories.set(`${record.kind}:${record.key}`, record);
  }

  async deleteMemory(kind: MemoryKind, key: string): Promise<void> {
    this.memories.delete(`${kind}:${key}`);
  }

  async getMemory(kind: MemoryKind, key: string): Promise<MemoryRecord | null> {
    return this.memories.get(`${kind}:${key}`) ?? null;
  }

  async listMemories(): Promise<readonly MemoryRecord[]> {
    return [...this.memories.values()];
  }

  async openSession(record: WorkSessionRecord): Promise<void> {
    this.sessions.set(record.sessionId, record);
  }

  async closeSession(
    sessionId: string,
    endedAt: string,
    termination: "completed" | "abandoned",
    episodeCount: number,
  ): Promise<void> {
    const current = this.sessions.get(sessionId);
    if (!current) return;
    this.sessions.set(sessionId, { ...current, endedAt, termination, episodeCount });
  }

  async currentSession(): Promise<WorkSessionRecord | null> {
    for (const session of this.sessions.values()) {
      if (session.termination === "open") return session;
    }
    return null;
  }

  async listSessions(): Promise<readonly WorkSessionRecord[]> {
    return [...this.sessions.values()];
  }

  async putEpisode(record: EpisodeRecord): Promise<void> {
    if (this.episodes.some((episode) => episode.episodeId === record.episodeId)) return;
    this.episodes.push(record);
  }

  async listEpisodes(): Promise<readonly EpisodeRecord[]> {
    return this.episodes;
  }

  async recordCompletedEpisode(record: EpisodeRecord): Promise<PatternRecord | null> {
    await this.putEpisode(record);
    if (record.outcome !== "completed") return null;
    const pattern = foldPattern(await this.getPattern(record.signature), record);
    await this.putPattern(pattern);
    return pattern;
  }

  async putReceipt(record: ReceiptRecord): Promise<void> {
    this.receipts.push(record);
  }

  async listReceipts(): Promise<readonly ReceiptRecord[]> {
    return this.receipts;
  }

  async putPattern(record: PatternRecord): Promise<void> {
    this.patterns.set(record.signature, record);
  }

  async getPattern(signature: string): Promise<PatternRecord | null> {
    return this.patterns.get(signature) ?? null;
  }

  async listPatterns(): Promise<readonly PatternRecord[]> {
    return [...this.patterns.values()];
  }

  async putCandidate(record: CandidateRecord): Promise<void> {
    this.candidates.set(record.candidateId, record);
  }

  async listCandidates(): Promise<readonly CandidateRecord[]> {
    return [...this.candidates.values()];
  }

  async putPatternEvidence(record: PatternEvidenceRecord): Promise<void> {
    if (this.evidenceEvents.some((row) => row.evidenceId === record.evidenceId)) return;
    this.evidenceEvents.push(record);
  }

  async listPatternEvidence(signature?: string): Promise<readonly PatternEvidenceRecord[]> {
    if (!signature) return [...this.evidenceEvents];
    return this.evidenceEvents.filter((row) => row.signature === signature);
  }

  async putReview(record: ReviewRecord): Promise<void> {
    this.reviews.push(record);
  }

  async listReviews(): Promise<readonly ReviewRecord[]> {
    return this.reviews;
  }

  async compact(nowIso: string): Promise<number> {
    const cutoff = Date.parse(nowIso) - RETENTION_MS;
    const before = this.episodes.length;
    this.episodes = this.episodes.filter((episode) => {
      if (episode.outcome !== "abandoned" || !episode.completedAt) return true;
      return Date.parse(episode.completedAt) >= cutoff;
    });
    return before - this.episodes.length;
  }
}
