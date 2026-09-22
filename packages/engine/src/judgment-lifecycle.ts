import type {
  ArtifactStorePort,
  JudgmentAnswer,
  JudgmentFailure,
  JudgmentPort,
  JudgmentRecord,
  JudgmentRequest,
  JudgmentResponse,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import type { EngineStore } from "./store.js";
import type { Clock, IdFactory } from "./scheduler.js";

export type JudgmentLifecycleDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  /**
   * When false, do not dispatch to Jev; return hosted_processing_disabled.
   * Missing callback fails closed (not authorized). Thrown errors → not_authorized.
   */
  readonly isHostedProcessingAllowed?: () => boolean | Promise<boolean>;
};

function encode(value: unknown): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(value));
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export async function canonicalizeRequestHash(request: JudgmentRequest): Promise<string> {
  const canonical = {
    provider: request.provider ?? "",
    model: request.model,
    questionSetId: request.questionSetId,
    questionSetVersion: request.questionSetVersion,
    questions: Object.fromEntries(
      Object.entries(request.questions).map(([key, question]) => [
        key,
        { type: question.type, ...(question.type === "choice" ? { optionIds: Object.keys(question.criteria) } : {}) },
      ]),
    ),
    state: sanitizeState(
      request.state && typeof request.state === "object" ? (request.state as Record<string, unknown>) : {},
    ),
    stateDigest: await sha256Hex(encode(request.state ?? null)),
    sources: (request.sourceObjectRefs ?? []).map((s) => s.sha256).sort(),
  };
  return sha256Hex(encode(canonical));
}

function sanitizeState(state: Readonly<Record<string, unknown>>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(state)) {
    if (typeof value === "number" || typeof value === "boolean") out[key] = value;
    else if (typeof value === "string" && /^[a-zA-Z0-9._:-]{1,64}$/.test(value)) out[key] = value;
    else if (Array.isArray(value) && value.every((item) => typeof item === "string" && item.length <= 32)) {
      out[key] = value;
    }
  }
  return out;
}

function safeRequestArtifact(request: JudgmentRequest, requestHash: string): Uint8Array {
  return encode({
    questionSetId: request.questionSetId,
    questionSetVersion: request.questionSetVersion,
    model: request.model,
    provider: request.provider ?? null,
    requestHash,
    state: sanitizeState(
      request.state && typeof request.state === "object" ? (request.state as Record<string, unknown>) : {},
    ),
    questionTypes: Object.fromEntries(
      Object.entries(request.questions).map(([key, question]) => [key, question.type]),
    ),
  });
}

function safeResponseArtifact(response: JudgmentResponse): Uint8Array {
  if (!response.ok) {
    return encode({
      ok: false,
      category: response.failure.category,
      httpStatus: response.failure.httpStatus ?? null,
      ...(response.failure.providerRequestId
        ? { providerRequestId: response.failure.providerRequestId }
        : {}),
    });
  }
  const answers: Record<string, unknown> = {};
  for (const [key, answer] of Object.entries(response.success.answers)) {
    if (answer.type === "choice") {
      answers[key] = {
        type: "choice",
        choice: answer.choice,
        probabilities: answer.probabilities,
      };
    } else if (answer.type === "noul") {
      answers[key] = { type: "noul", probabilityYes: answer.probabilityYes };
    } else {
      answers[key] = { type: answer.type };
    }
  }
  return encode({
    ok: true,
    model: response.success.model,
    answers,
    inputTokens: response.success.inputTokens,
    outputTokens: response.success.outputTokens,
    elapsedMs: response.success.elapsedMs,
    ...(response.success.providerRequestId
      ? { providerRequestId: response.success.providerRequestId }
      : {}),
  });
}

/**
 * Persist request → dispatch → persist response → return.
 * Artifacts store only allowlisted fields (no instruction prose).
 */
export async function runJudgmentLifecycle(
  deps: JudgmentLifecycleDeps,
  request: JudgmentRequest,
  signal: AbortSignal,
): Promise<{ record: JudgmentRecord; response: JudgmentResponse; providerCalled: boolean }> {
  const requestHash = request.requestHash ?? (await canonicalizeRequestHash(request));
  const cached = await deps.store.findCompletedJudgmentByHash(requestHash);
  if (cached?.responseArtifactId) {
    try {
      const raw = await deps.artifacts.get({
        artifactId: cached.responseArtifactId,
        sha256: cached.responseHash ?? "",
        policy: localOnlyPolicy(),
      });
      const stored = JSON.parse(new TextDecoder().decode(raw)) as {
        ok?: boolean;
        model?: string;
        answers?: Record<string, JudgmentAnswer>;
        inputTokens?: number;
        outputTokens?: number;
        elapsedMs?: number;
        category?: string;
      };
      if (stored.ok === true && stored.answers && typeof stored.answers === "object") {
        const response: JudgmentResponse = {
          ok: true,
          success: {
            model: stored.model ?? cached.model,
            answers: stored.answers,
            inputTokens: Number(stored.inputTokens ?? 0),
            outputTokens: Number(stored.outputTokens ?? 0),
            elapsedMs: Number(stored.elapsedMs ?? 0),
          },
        };
        return { record: cached, response, providerCalled: false };
      }
    } catch {
      // Fall through to a fresh provider call when the safe artifact cannot be read.
    }
  }

  const requestBytes = safeRequestArtifact(request, requestHash);
  const requestArtifact = await deps.artifacts.put(requestBytes, localOnlyPolicy());
  const judgmentId = deps.ids.next("jud");
  const createdAt = deps.clock.now().toISOString();
  const requested: JudgmentRecord = {
    judgmentId,
    questionSetId: request.questionSetId,
    questionSetVersion: request.questionSetVersion,
    model: request.model,
    status: "requested",
    createdAt,
    requestArtifactId: requestArtifact.artifactId,
    requestHash,
    ...(request.caseId ? { caseId: request.caseId } : {}),
    ...(request.caseVersion != null ? { caseVersion: request.caseVersion } : {}),
    ...(request.provider ? { provider: request.provider } : {}),
  };
  await deps.store.upsertJudgment(requested);

  let allowed = false;
  let gateFailure: JudgmentFailure | null = null;
  if (deps.isHostedProcessingAllowed == null) {
    allowed = false;
    gateFailure = { category: "not_authorized", message: "hosted_processing_disabled" };
  } else {
    try {
      allowed = await deps.isHostedProcessingAllowed();
      if (!allowed) {
        gateFailure = { category: "disabled", message: "hosted_processing_disabled" };
      }
    } catch {
      allowed = false;
      gateFailure = { category: "not_authorized", message: "hosted_processing_disabled" };
    }
  }
  if (!allowed && gateFailure) {
    const response: JudgmentResponse = {
      ok: false,
      failure: gateFailure,
    };
    const responseArtifact = await deps.artifacts.put(safeResponseArtifact(response), localOnlyPolicy());
    const completedAt = deps.clock.now().toISOString();
    const completed: JudgmentRecord = {
      ...requested,
      status: "failed",
      responseArtifactId: responseArtifact.artifactId,
      responseHash: responseArtifact.sha256,
      completedAt,
      failureCategory:
        gateFailure.category === "not_authorized" ? "not_authorized" : "hosted_processing_disabled",
    };
    await deps.store.upsertJudgment(completed);
    return { record: completed, response, providerCalled: false };
  }

  const response = await deps.judgments.judge({ ...request, requestHash }, signal);

  const responseArtifact = await deps.artifacts.put(safeResponseArtifact(response), localOnlyPolicy());
  const completedAt = deps.clock.now().toISOString();
  const completed: JudgmentRecord = {
    ...requested,
    status: response.ok ? "completed" : "failed",
    responseArtifactId: responseArtifact.artifactId,
    responseHash: responseArtifact.sha256,
    completedAt,
    ...(response.ok
      ? {
          inputTokens: response.success.inputTokens,
          outputTokens: response.success.outputTokens,
          elapsedMs: response.success.elapsedMs,
        }
      : { failureCategory: response.failure.category }),
  };
  await deps.store.upsertJudgment(completed);
  return { record: completed, response, providerCalled: true };
}
