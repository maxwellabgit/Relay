import type { TextModelPort } from "@relay/contracts";
import { SUMMARIZE_PROMPT_V1 } from "../prompts/summarize.v1.js";
import { draftDirectAnswer } from "./direct-answer.js";
import { generateTyped, type TypedGeneration } from "./typed-repair.js";

const EXPLICIT_SUMMARY = /^summarize:\s+([\s\S]{1,4000})$/i;

/** Source text from an explicit summarize command. Other asks stay on the direct-answer path. */
export function explicitSummarySource(text: string): string | null {
  const match = EXPLICIT_SUMMARY.exec(text.trim());
  const source = match?.[1]?.trim() ?? "";
  return source.length > 0 ? source : null;
}

/** A short summary stays inside the source. It does not repeat the instructions. */
export function acceptShortSummary(text: string): boolean {
  const summary = text.trim();
  if (summary.length < 1 || summary.length > 600) return false;
  if (/do not add facts/i.test(summary)) return false;
  if (/summarize the following/i.test(summary)) return false;
  return true;
}

export async function draftShortSummary(input: {
  readonly model: TextModelPort;
  readonly source: string;
  readonly signal: AbortSignal;
  readonly caseId?: string;
}): Promise<TypedGeneration> {
  return generateTyped({
    model: input.model,
    signal: input.signal,
    request: {
      taskKind: SUMMARIZE_PROMPT_V1.taskKind,
      promptVersion: SUMMARIZE_PROMPT_V1.promptVersion,
      prompt: SUMMARIZE_PROMPT_V1.build(input.source),
      maxTokens: 220,
      temperature: 0.2,
      ...(input.caseId ? { caseId: input.caseId } : {}),
    },
    accept: acceptShortSummary,
    repairPrompt: (invalidText) =>
      [
        "The previous summary was empty or repeated the instructions.",
        "Summarize only the source text in one or two sentences.",
        `Previous: ${invalidText.slice(0, 200)}`,
        `Source: ${input.source.slice(0, 400)}`,
      ].join("\n"),
  });
}

/** Explicit `summarize:` uses the summary prompt. Every other ask stays a direct answer. */
export async function draftAskProse(input: {
  readonly model: TextModelPort;
  readonly ask: string;
  readonly signal: AbortSignal;
  readonly caseId?: string;
}): Promise<TypedGeneration> {
  const source = explicitSummarySource(input.ask);
  if (!source) return draftDirectAnswer(input);
  return draftShortSummary({
    model: input.model,
    source,
    signal: input.signal,
    ...(input.caseId ? { caseId: input.caseId } : {}),
  });
}
