export type JudgmentQuestion =
  | {
      readonly type: "noul";
      readonly instructions: string;
      readonly criteria?: Readonly<Record<string, string>>;
    }
  | {
      readonly type: "choice";
      readonly instructions: string;
      readonly criteria: Readonly<Record<string, string>>;
      readonly requireNoMatch?: boolean;
    }
  | {
      readonly type: "score";
      readonly instructions: string;
      readonly criteria: readonly string[];
    };

export const CHOICE_NO_MATCH = "no_match" as const;

export type JudgmentAnswer =
  | { readonly type: "noul"; readonly probabilityYes: number }
  | {
      readonly type: "choice";
      readonly choice: string;
      readonly probabilities: Readonly<Record<string, number>>;
      readonly confidence: number;
    }
  | {
      readonly type: "score";
      readonly score: number;
      readonly legend: Readonly<Record<string, string>>;
      readonly probabilities: Readonly<Record<string, number>>;
      readonly confidence: number;
    };

export type JudgmentSourceRef = {
  readonly artifactId: string;
  readonly sha256: string;
  readonly classification?: string;
};

export type JudgmentRequest = {
  readonly questionSetId: string;
  readonly questionSetVersion: string;
  readonly model: string;
  readonly state: unknown;
  readonly questions: Readonly<Record<string, JudgmentQuestion>>;
  readonly sourceObjectRefs?: readonly JudgmentSourceRef[];
  readonly caseId?: string;
  readonly caseVersion?: number;
  readonly disclosureGrantId?: string;
  readonly requestHash?: string;
  readonly provider?: string;
};

export type JudgmentFailureCategory =
  | "disabled"
  | "not_authorized"
  | "missing_secret"
  | "timeout"
  | "rate_limited"
  | "overloaded"
  | "authentication"
  | "validation"
  | "invalid_response"
  | "network"
  | "cancelled";

export type JudgmentSuccess = {
  readonly model: string;
  readonly answers: Readonly<Record<string, JudgmentAnswer>>;
  readonly inputTokens: number;
  readonly outputTokens: number;
  readonly elapsedMs: number;
  readonly providerRequestId?: string;
};

export type JudgmentFailure = {
  readonly category: JudgmentFailureCategory;
  readonly message: string;
  readonly httpStatus?: number;
};

export type JudgmentResponse =
  | { readonly ok: true; readonly success: JudgmentSuccess }
  | { readonly ok: false; readonly failure: JudgmentFailure };

export type JudgmentStatus = "requested" | "completed" | "failed" | "deferred";

export type JudgmentRecord = {
  readonly judgmentId: string;
  readonly provider?: string;
  readonly questionSetId: string;
  readonly questionSetVersion: string;
  readonly model: string;
  readonly status: JudgmentStatus;
  readonly caseId?: string;
  readonly caseVersion?: number;
  readonly requestArtifactId?: string;
  readonly requestHash?: string;
  readonly responseArtifactId?: string;
  readonly responseHash?: string;
  readonly failureCategory?: string;
  readonly inputTokens?: number;
  readonly outputTokens?: number;
  readonly elapsedMs?: number;
  readonly createdAt: string;
  readonly completedAt?: string;
};

export function isRetryableJudgmentFailure(category: JudgmentFailureCategory): boolean {
  return (
    category === "timeout" ||
    category === "rate_limited" ||
    category === "overloaded" ||
    category === "network"
  );
}

export function validateQuestion(question: JudgmentQuestion): string | null {
  if (!question.instructions.trim()) return "empty_instructions";
  if (question.type === "choice") {
    const keys = Object.keys(question.criteria);
    if (keys.length === 0) return "empty_choice_criteria";
    const requireNoMatch = question.requireNoMatch !== false;
    if (requireNoMatch && !("no_match" in question.criteria)) return "missing_no_match";
  }
  if (question.type === "score" && question.criteria.length === 0) return "empty_score_criteria";
  return null;
}

export function validateAnswerProbability(value: number): boolean {
  return Number.isFinite(value) && value >= 0 && value <= 1;
}
