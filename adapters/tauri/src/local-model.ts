import type { GenerationRequest, GenerationResponse, TextModelPort } from "@relay/contracts";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

export type LocalModelStatus = {
  readonly ok: boolean;
  readonly detail: string;
  readonly model?: string | null;
};

/**
 * Narrow Tauri adapter for the fixed local llama.cpp-compatible loopback service.
 * Windows V1 uses an externally started server (see `dev/start-model.ps1`).
 * Native code owns the endpoint; this port does not launch processes.
 */
export class TauriLocalModelPort implements TextModelPort {
  constructor(private readonly invoke: TauriInvoke) {}

  async status(): Promise<LocalModelStatus> {
    const result = (await this.invoke("local_model_status")) as LocalModelStatus;
    const detail = result?.ok
      ? "external:ready"
      : (result?.detail ?? "external:unavailable");
    return {
      ok: result?.ok === true,
      detail,
      model: result?.model ?? null,
    };
  }

  async generate(request: GenerationRequest, signal: AbortSignal): Promise<GenerationResponse> {
    if (signal.aborted) {
      return { ok: false, failureReason: "cancelled" };
    }
    try {
      const result = (await this.invoke("local_model_generate", {
        request: {
          task_kind: request.taskKind,
          prompt_version: request.promptVersion ?? null,
          prompt: request.prompt,
          max_tokens: request.maxTokens ?? 512,
          temperature: request.temperature ?? 0.2,
        },
      })) as {
        ok: boolean;
        text?: string;
        model?: string;
        elapsed_ms?: number;
        failure_reason?: string;
      };
      if (signal.aborted) {
        return { ok: false, failureReason: "cancelled" };
      }
      if (!result?.ok) {
        return {
          ok: false,
          failureReason: (result?.failure_reason as GenerationResponse extends {
            ok: false;
            failureReason: infer R;
          }
            ? R
            : string) || "model_unavailable",
        };
      }
      if (typeof result.text !== "string" || !result.text.trim()) {
        return { ok: false, failureReason: "invalid_response" };
      }
      return {
        ok: true,
        text: result.text.trim(),
        model: result.model ?? "local",
        elapsedMs: Number(result.elapsed_ms ?? 0),
      };
    } catch {
      if (signal.aborted) return { ok: false, failureReason: "cancelled" };
      return { ok: false, failureReason: "model_unavailable" };
    }
  }
}
