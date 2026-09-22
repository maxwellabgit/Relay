import type { GenerationRequest, TextModelPort } from "@relay/contracts";

/** One repair after an invalid typed draft. A third call is not made. */
export const MAX_TYPED_REPAIRS = 1;

export type TypedGeneration =
  | { readonly ok: true; readonly text: string; readonly attempts: number }
  | {
      readonly ok: false;
      readonly reason: "invalid_response" | "cancelled" | "model_unavailable";
      readonly attempts: number;
    };

export async function generateTyped(input: {
  readonly model: TextModelPort;
  readonly request: GenerationRequest;
  readonly signal: AbortSignal;
  readonly accept: (text: string) => boolean;
  readonly repairPrompt: (invalidText: string) => string;
}): Promise<TypedGeneration> {
  if (input.signal.aborted) return { ok: false, reason: "cancelled", attempts: 0 };
  const first = await input.model.generate(input.request, input.signal);
  if (input.signal.aborted) return { ok: false, reason: "cancelled", attempts: 1 };
  if (!first.ok) {
    return {
      ok: false,
      reason: first.failureReason === "cancelled" ? "cancelled" : "model_unavailable",
      attempts: 1,
    };
  }
  if (input.accept(first.text)) return { ok: true, text: first.text, attempts: 1 };
  if (input.signal.aborted) return { ok: false, reason: "cancelled", attempts: 1 };

  const second = await input.model.generate(
    {
      ...input.request,
      prompt: input.repairPrompt(first.text),
    },
    input.signal,
  );
  if (input.signal.aborted) return { ok: false, reason: "cancelled", attempts: 2 };
  if (!second.ok || !input.accept(second.text)) {
    return { ok: false, reason: "invalid_response", attempts: 2 };
  }
  return { ok: true, text: second.text, attempts: 2 };
}
