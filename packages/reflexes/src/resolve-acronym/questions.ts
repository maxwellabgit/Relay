import { CHOICE_NO_MATCH, type JudgmentQuestion } from "@relay/contracts";

export function buildAcronymQuestions(candidates: readonly string[]): Record<string, JudgmentQuestion> {
  const criteria: Record<string, string> = {};
  for (const c of candidates) criteria[c] = c;
  criteria[CHOICE_NO_MATCH] = "No matching expansion";
  return {
    expansion: {
      type: "choice",
      instructions:
        "Select the expansion that matches the acronym in the supplied candidates. Do not invent expansions.",
      criteria,
      requireNoMatch: true,
    },
    useful: {
      type: "noul",
      instructions:
        "Is showing an expansion for this acronym useful in the current conversational context?",
    },
  };
}
