import type {
  DecisionGateView,
  ExpansionProgress,
  GateRow,
  KeptDecision,
  PatternRecommendation,
} from "@relay/contracts";

export const SESSION_REVIEW_TARGET = 12;
export const REFLEX_REVIEW_TARGET = 4;
export const REPEAT_BEFORE_RECOMMEND = 3;

/** Published resolve-acronym@1 gates. Shown even when Jev is not called. */
export const ACRONYM_GATES = {
  choiceProbabilityMinimum: 0.65,
  choiceMarginMinimum: 0.15,
  usefulnessMinimum: 0.7,
  rememberYesMinimum: 0.7,
} as const;

export const DECISION_LOG_PATH = ".dev-data/dev-console/decisions.jsonl";

const KEPT_CODES = new Set([
  "session.started",
  "session.completed",
  "lookup.exact",
  "lookup.unknown",
  "gate.judgment_required",
  "gate.remember",
  "pattern.candidate",
  "expansion.checked",
]);

export type DecisionLogPort = {
  readonly directoryLabel: string;
  append(record: KeptDecision): Promise<void>;
  read(): Promise<readonly KeptDecision[]>;
  replace(records: readonly KeptDecision[]): Promise<void>;
};

export function isKeptDecision(value: unknown): value is KeptDecision {
  if (!value || typeof value !== "object") return false;
  const row = value as Record<string, unknown>;
  if (typeof row.sequence !== "number" || !Number.isFinite(row.sequence)) return false;
  if (typeof row.at !== "string" || !row.at) return false;
  if (typeof row.code !== "string" || !KEPT_CODES.has(row.code)) return false;
  if (!isSafeDetail(row.detail)) return false;
  if ("text" in row || "prompt" in row || "response" in row || "summary" in row) return false;
  return true;
}

export function parseDecisionLog(text: string): KeptDecision[] {
  const kept: KeptDecision[] = [];
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    try {
      const parsed: unknown = JSON.parse(trimmed);
      if (isKeptDecision(parsed)) kept.push(parsed);
    } catch {
      // Trash line. Drop it.
    }
  }
  return kept;
}

export class DecisionLedger {
  private readonly records: KeptDecision[] = [];
  private sequence = 0;
  private sessionOpen = false;
  private workInSession = 0;
  private completeSessions = 0;
  private unknownLookups = 0;
  private exactLookups = 0;
  private gate: DecisionGateView | null = null;
  private readonly recommended = new Map<string, PatternRecommendation>();

  constructor(
    private readonly reflexCount: number,
    private readonly now: () => string,
    private readonly log?: DecisionLogPort,
  ) {}

  async hydrate(records: readonly KeptDecision[]): Promise<void> {
    for (const record of records) {
      if (!isKeptDecision(record)) continue;
      this.records.push(record);
      this.sequence = Math.max(this.sequence, record.sequence);
      this.apply(record);
    }
  }

  async startSession(): Promise<"started" | "already_open"> {
    if (this.sessionOpen) return "already_open";
    this.sessionOpen = true;
    this.workInSession = 0;
    await this.push("session.started", "open", null);
    return "started";
  }

  async endSession(): Promise<"completed" | "discarded" | "no_session"> {
    if (!this.sessionOpen) return "no_session";
    this.sessionOpen = false;
    if (this.workInSession === 0) {
      this.dropOpenSession();
      await this.log?.replace(this.records);
      return "discarded";
    }
    this.completeSessions += 1;
    await this.push("session.completed", `${this.completeSessions}/${SESSION_REVIEW_TARGET}`, null);
    await this.push("expansion.checked", this.expansionDetail(), null);
    return "completed";
  }

  async recordExactLookup(token: string): Promise<void> {
    this.exactLookups += 1;
    this.workInSession += 1;
    const gate = this.gateView(`Acronym · ${token}`, [
      row("Reflex", "resolve-acronym@1", "info"),
      row("Path", "exact glossary", "pass"),
      row("Jev", "not called", "info"),
      row("Choice minimum", formatThreshold(ACRONYM_GATES.choiceProbabilityMinimum), "info"),
      row("Choice margin", formatThreshold(ACRONYM_GATES.choiceMarginMinimum), "info"),
      row("Usefulness Noul", formatThreshold(ACRONYM_GATES.usefulnessMinimum), "info"),
      row("Result", "bypassed", "pass"),
    ]);
    await this.push("lookup.exact", `${token} · glossary · Jev bypassed`, gate);
    await this.maybeRecommend("glossary_pin", "repeated_exact_lookup", this.exactLookups);
  }

  async recordUnknownLookup(token: string): Promise<void> {
    this.unknownLookups += 1;
    this.workInSession += 1;
    const gate = this.gateView(`Acronym · ${token}`, [
      row("Reflex", "resolve-acronym@1", "info"),
      row("Path", "no candidates", "fail"),
      row("Jev", "not called", "info"),
      row("Choice minimum", formatThreshold(ACRONYM_GATES.choiceProbabilityMinimum), "info"),
      row("Usefulness Noul", formatThreshold(ACRONYM_GATES.usefulnessMinimum), "info"),
      row("Result", "search task, not a definition", "wait"),
    ]);
    await this.push("lookup.unknown", `${token} · no candidates · Jev not called`, gate);
    await this.maybeRecommend("glossary_tool", "repeated_unknown_lookup", this.unknownLookups);
  }

  async recordJudgmentRequired(token: string): Promise<void> {
    this.workInSession += 1;
    const gate = this.gateView(`Acronym · ${token}`, [
      row("Reflex", "resolve-acronym@1", "info"),
      row("Question", "choice expansion + usefulness Noul", "wait"),
      row("Choice minimum", formatThreshold(ACRONYM_GATES.choiceProbabilityMinimum), "wait"),
      row("Choice margin", formatThreshold(ACRONYM_GATES.choiceMarginMinimum), "wait"),
      row("Usefulness Noul", formatThreshold(ACRONYM_GATES.usefulnessMinimum), "wait"),
      row("Jev", "required before a definition is shown", "wait"),
    ]);
    await this.push(
      "gate.judgment_required",
      `${token} · choice ${ACRONYM_GATES.choiceProbabilityMinimum.toFixed(2)} · margin ${ACRONYM_GATES.choiceMarginMinimum.toFixed(2)} · usefulness ${ACRONYM_GATES.usefulnessMinimum.toFixed(2)}`,
      gate,
    );
  }

  async recordRemember(
    token: string,
    outcome:
      | { readonly kind: "missing"; readonly category: string }
      | { readonly kind: "noul"; readonly probabilityYes: number; readonly accept: boolean },
  ): Promise<void> {
    this.workInSession += 1;
    if (outcome.kind === "missing") {
      const gate = this.gateView(`Remember · ${token}`, [
        row("Question", "judgment.remember Noul", "info"),
        row("Threshold", formatThreshold(ACRONYM_GATES.rememberYesMinimum), "info"),
        row("Jev", outcome.category, "fail"),
        row("Result", "not accepted", "fail"),
      ]);
      await this.push("gate.remember", `${token} · ${outcome.category} · not accepted`, gate);
      return;
    }
    const mark = outcome.accept ? "pass" : "fail";
    const gate = this.gateView(`Remember · ${token}`, [
      row("Question", "judgment.remember Noul", "info"),
      row("P(yes)", outcome.probabilityYes.toFixed(2), mark),
      row("P(no)", (1 - outcome.probabilityYes).toFixed(2), "info"),
      row("Threshold", formatThreshold(ACRONYM_GATES.rememberYesMinimum), "info"),
      row(
        "Interval",
        `${Math.min(outcome.probabilityYes, 1 - outcome.probabilityYes).toFixed(2)}–${Math.max(outcome.probabilityYes, 1 - outcome.probabilityYes).toFixed(2)}`,
        "info",
      ),
      row("Result", outcome.accept ? "accepted" : "below threshold", mark),
    ]);
    await this.push(
      "gate.remember",
      `${token} · noul ${outcome.probabilityYes.toFixed(2)} · threshold ${ACRONYM_GATES.rememberYesMinimum.toFixed(2)} · ${outcome.accept ? "pass" : "fail"}`,
      gate,
    );
  }

  view(logPath = this.log?.directoryLabel ?? DECISION_LOG_PATH): {
    gate: DecisionGateView | null;
    expansion: ExpansionProgress;
    recommendations: readonly PatternRecommendation[];
    decisions: readonly KeptDecision[];
    decisionLogPath: string;
  } {
    return {
      gate: this.gate,
      expansion: this.expansion(),
      recommendations: [...this.recommended.values()],
      decisions: this.records.slice(-80),
      decisionLogPath: logPath,
    };
  }

  private expansion(): ExpansionProgress {
    const reflexesBuilt = this.reflexCount;
    return {
      completeSessions: this.completeSessions,
      sessionTarget: SESSION_REVIEW_TARGET,
      reflexesBuilt,
      reflexTarget: REFLEX_REVIEW_TARGET,
      reviewDue:
        this.completeSessions >= SESSION_REVIEW_TARGET && reflexesBuilt >= REFLEX_REVIEW_TARGET,
    };
  }

  private expansionDetail(): string {
    const progress = this.expansion();
    return `sessions ${progress.completeSessions}/${progress.sessionTarget} · reflexes ${progress.reflexesBuilt}/${progress.reflexTarget} · ${progress.reviewDue ? "review due" : "not due"}`;
  }

  private async maybeRecommend(code: string, because: string, count: number): Promise<void> {
    if (count < REPEAT_BEFORE_RECOMMEND) return;
    const key = `${code}:${because}`;
    if (this.recommended.has(key)) return;
    const recommendation: PatternRecommendation = { code, because, count, status: "candidate" };
    this.recommended.set(key, recommendation);
    await this.push("pattern.candidate", `${code} · ${because} · ${count}`, null);
  }

  private async push(
    code: string,
    detail: string,
    gate: DecisionGateView | null,
  ): Promise<void> {
    if (!isSafeDetail(detail) || !KEPT_CODES.has(code)) return;
    const last = this.records[this.records.length - 1];
    if (last && last.code === code && last.detail === detail) return;
    const record: KeptDecision = {
      sequence: ++this.sequence,
      at: this.now(),
      code,
      detail,
    };
    this.records.push(record);
    if (gate) this.gate = gate;
    await this.log?.append(record);
  }

  private dropOpenSession(): void {
    let start = -1;
    for (let index = this.records.length - 1; index >= 0; index -= 1) {
      if (this.records[index]?.code === "session.started") {
        start = index;
        break;
      }
    }
    if (start < 0) return;
    const span = this.records.slice(start);
    if (span.some((record) => isWorkCode(record.code))) return;
    this.records.splice(start, span.length);
  }

  private apply(record: KeptDecision): void {
    if (record.code === "session.completed") this.completeSessions += 1;
    if (record.code === "lookup.unknown") {
      this.unknownLookups += 1;
      this.gate = this.restoredGate(record, `Acronym · ${head(record.detail)}`, [
        row("Path", "no candidates", "fail"),
        row("Jev", "not called", "info"),
        row("Usefulness Noul", formatThreshold(ACRONYM_GATES.usefulnessMinimum), "info"),
        row("Kept", record.detail, "info"),
      ]);
    }
    if (record.code === "lookup.exact") {
      this.exactLookups += 1;
      this.gate = this.restoredGate(record, `Acronym · ${head(record.detail)}`, [
        row("Path", "exact glossary", "pass"),
        row("Jev", "not called", "info"),
        row("Result", "bypassed", "pass"),
        row("Kept", record.detail, "info"),
      ]);
    }
    if (record.code === "gate.judgment_required") {
      this.gate = this.restoredGate(record, `Acronym · ${head(record.detail)}`, [
        row("Choice minimum", formatThreshold(ACRONYM_GATES.choiceProbabilityMinimum), "wait"),
        row("Choice margin", formatThreshold(ACRONYM_GATES.choiceMarginMinimum), "wait"),
        row("Usefulness Noul", formatThreshold(ACRONYM_GATES.usefulnessMinimum), "wait"),
        row("Jev", "required before a definition is shown", "wait"),
      ]);
    }
    if (record.code === "gate.remember") {
      const failed = record.detail.endsWith("fail") || record.detail.includes("not accepted");
      this.gate = this.restoredGate(record, `Remember · ${head(record.detail)}`, [
        row("Threshold", formatThreshold(ACRONYM_GATES.rememberYesMinimum), "info"),
        row("Kept", record.detail, failed ? "fail" : "pass"),
      ]);
    }
    if (record.code === "pattern.candidate") {
      const [code, because, count] = record.detail.split(" · ");
      if (code && because) {
        this.recommended.set(`${code}:${because}`, {
          code,
          because,
          count: Number(count) || REPEAT_BEFORE_RECOMMEND,
          status: "candidate",
        });
      }
    }
  }

  private restoredGate(record: KeptDecision, title: string, rows: readonly GateRow[]): DecisionGateView {
    return { at: record.at, title, rows };
  }

  private gateView(title: string, rows: readonly GateRow[]): DecisionGateView {
    return { at: this.now(), title, rows };
  }
}

function row(label: string, value: string, mark: GateRow["mark"]): GateRow {
  return { label, value, mark };
}

function formatThreshold(value: number): string {
  return value.toFixed(2);
}

function head(detail: string): string {
  return detail.split(" · ")[0] ?? detail;
}

function isWorkCode(code: string): boolean {
  return (
    code === "lookup.exact" ||
    code === "lookup.unknown" ||
    code === "gate.judgment_required" ||
    code === "gate.remember" ||
    code === "pattern.candidate"
  );
}

function isSafeDetail(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const detail = value.trim();
  if (!detail || detail.length > 160) return false;
  if (/[\n\r]/.test(detail)) return false;
  if (/search online|application programming interface/i.test(detail)) return false;
  return true;
}
