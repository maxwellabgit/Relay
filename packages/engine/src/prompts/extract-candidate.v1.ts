export const EXTRACT_CANDIDATE_PROMPT_V1 = {
  taskKind: "extract_candidate",
  promptVersion: "extract-candidate.v1",
  build(userText: string): string {
    return [
      "Extract a short structured candidate from the text.",
      "Return plain text only. Do not decide permissions or storage.",
      "",
      `Text: ${userText}`,
      "Candidate:",
    ].join("\n");
  },
} as const;
