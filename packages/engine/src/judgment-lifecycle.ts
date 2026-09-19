import type {
  ArtifactStorePort,
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
    questions: request.questions,
    state: request.state,
    sources: (request.sourceObjectRefs ?? []).map((s) => s.sha256).sort(),
  };
  return sha256Hex(encode(canonical));
}

/**
 * Persist request → dispatch → persist response → return.
 * Reuses matching completed results during replay/restart.
 * Provider calls happen outside storage transactions.
 */
export async function runJudgmentLifecycle(
  deps: JudgmentLifecycleDeps,
  request: JudgmentRequest,
  signal: AbortSignal,
): Promise<{ record: JudgmentRecord; response: JudgmentResponse; providerCalled: boolean }> {
  const requestHash = request.requestHash ?? (await canonicalizeRequestHash(request));
  const cached = await deps.store.findCompletedJudgmentByHash(requestHash);
  if (cached?.responseArtifactId) {
    const raw = await deps.artifacts.get({
      artifactId: cached.responseArtifactId,
      sha256: cached.responseHash ?? "",
      policy: localOnlyPolicy(),
    });
    const response = JSON.parse(new TextDecoder().decode(raw)) as JudgmentResponse;
    if (response.ok) {
      return { record: cached, response, providerCalled: false };
    }
  }

  const requestBytes = encode({ ...request, requestHash });
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

  // Provider call outside the persistence transaction boundary.
  const response = await deps.judgments.judge(
    { ...request, requestHash },
    signal,
  );

  const responseBytes = encode(response);
  const responseArtifact = await deps.artifacts.put(responseBytes, localOnlyPolicy());
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
