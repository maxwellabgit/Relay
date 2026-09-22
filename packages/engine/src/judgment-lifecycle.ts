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
import { evaluateHostedDisclosure, type DisclosureReceipt, type DisclosureScope, type DisclosureSource, type DisclosedSource, type HostedJudgmentGrant } from "./disclosure/hosted-grant.js";
import { claimSemanticRound } from "./disclosure/semantic-rounds.js";

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
  readonly disclosure?: {
    readonly grant: HostedJudgmentGrant | null;
    readonly now: string;
    readonly scope: DisclosureScope;
    readonly requestsUsed: number;
    readonly bytesUsed: number;
    readonly sources: readonly (DisclosureSource & DisclosedSource)[];
    readonly commit: (bytes: number) => Promise<void>;
    readonly attempt?: {
      beforeAttempt(requestBytes: number): Promise<{ ok: true; reservationId: string } | { ok: false; reason: string }>;
      commit(reservationId: string): Promise<void>;
      release(reservationId: string): Promise<void>;
    };
  };
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
    ...(request.disclosureGrantId ? { disclosureGrantId: request.disclosureGrantId } : {}),
    ...(request.disclosedSources ? { disclosedSources: request.disclosedSources } : {}),
    state: sanitizeState(
      request.state && typeof request.state === "object" ? (request.state as Record<string, unknown>) : {},
    ),
    questionTypes: Object.fromEntries(
      Object.entries(request.questions).map(([key, question]) => [key, question.type]),
    ),
  });
}

function safeResponseArtifact(response: JudgmentResponse, receipt?: DisclosureReceipt): Uint8Array {
  if (!response.ok) {
    return encode({
      ok: false,
      category: response.failure.category,
      httpStatus: response.failure.httpStatus ?? null,
      ...(response.failure.providerRequestId
        ? { providerRequestId: response.failure.providerRequestId }
        : {}),
      ...(receipt ? { disclosureReceipt: receipt } : {}),
      ...(response.failure.transport ? { transport: response.failure.transport } : {}),
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
    ...(receipt ? { disclosureReceipt: receipt } : {}),
    ...(response.success.transport ? { transport: response.success.transport } : {}),
  });
}

/**
 * Persist request → dispatch → persist response → return.
 * Artifacts store only allowlisted fields (no instruction prose).
 */
export type JudgmentBudgetEvidence = {
  readonly grantScopeKind: "session" | "project";
  readonly grantExpiresAt: string;
  readonly grantRequestsBefore: number;
  readonly grantRequestsAfter: number;
  readonly grantBytesBefore: number;
  readonly grantBytesAfter: number;
  readonly grantMaxRequests: number;
  readonly grantMaxBytes: number;
  readonly disclosedSourceCount: number;
  readonly disclosedBytes: number;
};

function budgetEvidence(
  disclosure: JudgmentLifecycleDeps["disclosure"],
  disclosedSourceCount: number,
  disclosedBytes: number,
  attemptCount: number,
): JudgmentBudgetEvidence | null {
  const grant = disclosure?.grant;
  if (!disclosure || !grant) return null;
  if (grant.scopeKind !== "session" && grant.scopeKind !== "project") return null;
  const attempts = Math.max(0, attemptCount);
  return {
    grantScopeKind: grant.scopeKind,
    grantExpiresAt: grant.expiresAt,
    grantRequestsBefore: disclosure.requestsUsed,
    grantRequestsAfter: disclosure.requestsUsed + attempts,
    grantBytesBefore: disclosure.bytesUsed,
    grantBytesAfter: disclosure.bytesUsed + disclosedBytes * attempts,
    grantMaxRequests: grant.maxRequests,
    grantMaxBytes: grant.maxBytes,
    disclosedSourceCount,
    disclosedBytes: attempts > 0 ? disclosedBytes : 0,
  };
}

function disclosureReceipt(
  sources: readonly { artifactId: string }[],
  decision: DisclosureReceipt["decision"],
  redactedBytes: number,
  grantId: string | null,
  physicalAttempts: number,
): DisclosureReceipt {
  return {
    artifactIds: sources.map((source) => source.artifactId),
    decision,
    redactedBytes,
    tokenEstimate: Math.ceil(redactedBytes / 4),
    grantId,
    physicalAttempts,
  };
}

export async function runJudgmentLifecycle(
  deps: JudgmentLifecycleDeps,
  request: JudgmentRequest,
  signal: AbortSignal,
): Promise<{
  record: JudgmentRecord;
  response: JudgmentResponse;
  providerCalled: boolean;
  disclosureGrantId: string | null;
  budget: JudgmentBudgetEvidence | null;
}> {
  let effectiveRequest = request;
  let disclosureFailure: JudgmentFailure | null = null;
  let disclosedSourceCount = 0;
  let disclosedBytes = 0;
  if (deps.disclosure) {
    const decision = evaluateHostedDisclosure({
      grant: deps.disclosure.grant,
      now: deps.disclosure.now,
      scope: deps.disclosure.scope,
      requestsUsed: deps.disclosure.requestsUsed,
      bytesUsed: deps.disclosure.bytesUsed,
      sources: deps.disclosure.sources,
      structuralState: request.state,
    });
    if (!decision.ok) {
      disclosureFailure = {
        category: "not_authorized",
        message: `disclosure_${decision.reason}`,
      };
    } else {
      disclosedSourceCount = decision.disclosed.length;
      disclosedBytes = decision.bytes;
      effectiveRequest = {
        ...request,
        state: decision.state,
        disclosureGrantId: decision.grantId,
        disclosedSources: decision.disclosed,
      };
    }
  }
  const disclosureGrantId = effectiveRequest.disclosureGrantId ?? null;
  const untouchedBudget = () => budgetEvidence(deps.disclosure, disclosedSourceCount, disclosedBytes, 0);
  const requestHash = effectiveRequest.requestHash ?? (await canonicalizeRequestHash(effectiveRequest));
  const cached = disclosureFailure ? null : await deps.store.findCompletedJudgmentByHash(requestHash);
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
        return { record: cached, response, providerCalled: false, disclosureGrantId, budget: untouchedBudget() };
      }
    } catch {
      // Fall through to a fresh provider call when the safe artifact cannot be read.
    }
  }

  const requestBytes = safeRequestArtifact(effectiveRequest, requestHash);
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
    ...(effectiveRequest.caseId ? { caseId: effectiveRequest.caseId } : {}),
    ...(effectiveRequest.caseVersion != null ? { caseVersion: effectiveRequest.caseVersion } : {}),
    ...(effectiveRequest.provider ? { provider: effectiveRequest.provider } : {}),
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
  if (allowed && disclosureFailure) {
    allowed = false;
    gateFailure = disclosureFailure;
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
    return { record: completed, response, providerCalled: false, disclosureGrantId, budget: untouchedBudget() };
  }

  if (effectiveRequest.caseId) {
    const claimed = await claimSemanticRound(
      deps.store,
      effectiveRequest.caseId,
      effectiveRequest.questionSetId,
      deps.clock.now().toISOString(),
    );
    if (!claimed.ok) {
      const response: JudgmentResponse = {
        ok: false,
        failure: { category: "not_authorized", message: claimed.reason },
      };
      const responseArtifact = await deps.artifacts.put(safeResponseArtifact(response), localOnlyPolicy());
      const completedAt = deps.clock.now().toISOString();
      const completed: JudgmentRecord = {
        ...requested,
        status: "failed",
        responseArtifactId: responseArtifact.artifactId,
        responseHash: responseArtifact.sha256,
        completedAt,
        failureCategory: "not_authorized",
      };
      await deps.store.upsertJudgment(completed);
      return { record: completed, response, providerCalled: false, disclosureGrantId, budget: untouchedBudget() };
    }
  }

  const selfAccounted = effectiveRequest.provider !== "recorded" && deps.disclosure?.attempt != null;
  if (selfAccounted && deps.disclosure?.attempt) {
    effectiveRequest = { ...effectiveRequest, physicalBudget: deps.disclosure.attempt };
  } else if (deps.disclosure) {
    await deps.disclosure.commit(disclosedBytes);
  }
  const response = await deps.judgments.judge({ ...effectiveRequest, requestHash }, signal);
  const physicalAttempts = response.ok
    ? (response.success.transport?.attempts ?? (selfAccounted ? 0 : 1))
    : (response.failure.transport?.attempts ?? (selfAccounted ? 0 : 1));
  const receipt = disclosureReceipt(
    deps.disclosure?.sources ?? [],
    "allow",
    disclosedBytes,
    disclosureGrantId,
    physicalAttempts,
  );

  const responseArtifact = await deps.artifacts.put(safeResponseArtifact(response, receipt), localOnlyPolicy());
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
  return {
    record: completed,
    response,
    providerCalled: (response.ok ? response.success.transport?.networkAttempted : response.failure.transport?.networkAttempted) ?? true,
    disclosureGrantId,
    budget: budgetEvidence(deps.disclosure, disclosedSourceCount, disclosedBytes, physicalAttempts),
  };
}
