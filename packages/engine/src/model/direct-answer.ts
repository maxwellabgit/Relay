import type { TextModelPort } from "@relay/contracts";
import { DIRECT_ANSWER_PROMPT_V1 } from "../prompts/direct-answer.v1.js";
import { generateTyped, type TypedGeneration } from "./typed-repair.js";

/** A direct reply is one short answer, not the instruction text. */
export function acceptDirectAnswer(text: string): boolean {
  const answer = text.trim();
  if (answer.length < 1 || answer.length > 600) return false;
  if (/do not invent/i.test(answer)) return false;
  return true;
}

export async function draftDirectAnswer(input: {
  readonly model: TextModelPort;
  readonly ask: string;
  readonly signal: AbortSignal;
  readonly caseId?: string;
}): Promise<TypedGeneration> {
  return generateTyped({
    model: input.model,
    signal: input.signal,
    request: {
      taskKind: DIRECT_ANSWER_PROMPT_V1.taskKind,
      promptVersion: DIRECT_ANSWER_PROMPT_V1.promptVersion,
      prompt: DIRECT_ANSWER_PROMPT_V1.build(input.ask),
      maxTokens: 220,
      temperature: 0.2,
      ...(input.caseId ? { caseId: input.caseId } : {}),
    },
    accept: acceptDirectAnswer,
    repairPrompt: (invalidText) =>
      [
        "The previous answer was empty or repeated the instructions.",
        "Answer in one or two sentences.",
        `Previous: ${invalidText.slice(0, 200)}`,
        `User: ${input.ask}`,
      ].join("\n"),
  });
}
