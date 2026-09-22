import type { TextModelPort } from "@relay/contracts";
import { generateTyped } from "./typed-repair.js";

/** One-line search query. A second invalid draft falls back to the Ask text. */
export function acceptSearchQuery(text: string): boolean {
  const query = text.trim().replace(/^["']|["']$/g, "");
  return query.length > 0 && query.length <= 120 && !query.includes("\n");
}

export async function draftSearchQuery(input: {
  readonly model: TextModelPort;
  readonly ask: string;
  readonly signal: AbortSignal;
}): Promise<string> {
  const fallback = input.ask.trim();
  const generated = await generateTyped({
    model: input.model,
    signal: input.signal,
    request: {
      taskKind: "tool_args",
      prompt: `Extract a short search query from this Ask. Reply with the query only.\n\nAsk: ${input.ask}`,
      maxTokens: 40,
      temperature: 0,
    },
    accept: acceptSearchQuery,
    repairPrompt: () => "Return one search query line. No quotes. No extra sentences.",
  });
  if (!generated.ok) return fallback;
  return generated.text.trim().replace(/^["']|["']$/g, "");
}
