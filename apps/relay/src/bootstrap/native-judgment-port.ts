import type { JudgmentPort, JudgmentRequest, JudgmentResponse } from "@relay/contracts";
import {
  attachJudgmentTransport,
  beginTransportReport,
  runTypeSafeAttempts,
  wireTypeSafeBody,
  type TypeSafeAttempt,
} from "@relay/engine";

export type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

type NativeJudgeOptions = {
  readonly maxAttempts?: number;
  readonly sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  readonly random?: () => number;
  readonly now?: () => number;
};

/**
 * Production Windows Jev port. Each native HTTP attempt reserves and commits
 * the physical grant before `typesafe_judge` runs. Cancellation before the
 * call releases the reservation. Cancellation during the call aborts the
 * native command and still counts the attempt that already started.
 */
export function createNativeJudgmentPort(
  invoke: TauriInvoke,
  options: NativeJudgeOptions = {},
): JudgmentPort {
  return {
    async judge(request: JudgmentRequest, signal: AbortSignal): Promise<JudgmentResponse> {
      const body = wireTypeSafeBody(request);
      const requestBytes = new TextEncoder().encode(JSON.stringify(body)).byteLength;
      const report = beginTransportReport(true, requestBytes);
      const response = await runTypeSafeAttempts({
        signal,
        questions: request.questions,
        ...(options.maxAttempts != null ? { maxAttempts: options.maxAttempts } : {}),
        ...(options.sleep ? { sleep: options.sleep } : {}),
        ...(options.random ? { random: options.random } : {}),
        ...(options.now ? { now: options.now } : {}),
        perform: () => performNativeAttempt(invoke, request, body, requestBytes, report, signal),
      });
      return attachJudgmentTransport(response, report);
    },
  };
}

async function performNativeAttempt(
  invoke: TauriInvoke,
  request: JudgmentRequest,
  body: ReturnType<typeof wireTypeSafeBody>,
  requestBytes: number,
  report: ReturnType<typeof beginTransportReport>,
  signal: AbortSignal,
): Promise<TypeSafeAttempt> {
  if (signal.aborted) return { kind: "transport", reason: "cancelled" };
  const budget = request.physicalBudget;
  let reservationId: string | null = null;
  if (budget) {
    const reserved = await budget.beforeAttempt(requestBytes);
    if (!reserved.ok) {
      return {
        kind: "terminal",
        failure: { category: "not_authorized", message: "grant_exhausted" },
      };
    }
    reservationId = reserved.reservationId;
  }
  if (signal.aborted) {
    if (reservationId && budget) await budget.release(reservationId);
    return { kind: "transport", reason: "cancelled" };
  }
  if (reservationId && budget) await budget.commit(reservationId);
  report.networkAttempted = true;
  report.attempts += 1;

  const onAbort = () => {
    void invoke("typesafe_cancel").catch(() => undefined);
  };
  signal.addEventListener("abort", onAbort, { once: true });
  try {
    const result = (await invoke("typesafe_judge", {
      request: { model: body.model, body },
    }).catch(() => {
      if (signal.aborted) return { ok: false, status: 0, category: "cancelled" };
      return { ok: false, status: 0, category: "network" };
    })) as {
      ok: boolean;
      status?: number;
      category?: string;
      latency_ms?: number;
      body?: unknown;
      retry_after?: string | null;
      request_id?: string | null;
    };
    if (signal.aborted || result?.category === "cancelled") {
      return { kind: "transport", reason: "cancelled" };
    }
    if (result?.category === "missing_secret") {
      return {
        kind: "terminal",
        failure: { category: "missing_secret", message: "typesafe_key_missing" },
      };
    }
    const status = Number(result?.status ?? (result?.ok ? 200 : 0));
    if (result?.ok && result.body) {
      return {
        kind: "http",
        status: status || 200,
        bodyText: JSON.stringify(result.body),
        retryAfter: result.retry_after ?? null,
        requestId: result.request_id ?? null,
        elapsedMs: Number(result.latency_ms ?? 0),
      };
    }
    return {
      kind: "http",
      status,
      bodyText: "",
      retryAfter: result?.retry_after ?? null,
      requestId: result?.request_id ?? null,
      elapsedMs: Number(result?.latency_ms ?? 0),
    };
  } finally {
    signal.removeEventListener("abort", onAbort);
  }
}
