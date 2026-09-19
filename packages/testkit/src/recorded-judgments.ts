import type {
  JudgmentPort,
  JudgmentRequest,
  JudgmentResponse,
  JudgmentSuccess,
} from "@relay/contracts";

export type RecordedJudgmentEntry = {
  readonly requestHash?: string;
  readonly questionSetId?: string;
  readonly response: JudgmentResponse;
};

export class RecordedJudgmentPort implements JudgmentPort {
  constructor(private readonly entries: readonly RecordedJudgmentEntry[] = []) {}

  async judge(request: JudgmentRequest, signal: AbortSignal): Promise<JudgmentResponse> {
    void signal;
    const byHash = request.requestHash
      ? this.entries.find((e) => e.requestHash === request.requestHash)
      : undefined;
    if (byHash) return byHash.response;

    const bySet = this.entries.find((e) => e.questionSetId === request.questionSetId);
    if (bySet) return bySet.response;

    // Missing key / no recording → disabled, never a fake positive.
    return {
      ok: false,
      failure: { category: "disabled", message: "recorded_judgment_missing" },
    };
  }
}

export function recordedSuccess(
  answers: JudgmentSuccess["answers"],
): JudgmentResponse {
  return {
    ok: true,
    success: {
      model: "recorded",
      answers,
      inputTokens: 0,
      outputTokens: 0,
      elapsedMs: 0,
    },
  };
}
