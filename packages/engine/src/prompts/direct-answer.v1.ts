export const DIRECT_ANSWER_PROMPT_V1 = {
  taskKind: "direct_answer",
  promptVersion: "direct-answer.v1",
  build(userText: string): string {
    return [
      "You are RELAY's local assistant.",
      "Answer the user's question directly and concisely.",
      "Do not invent tool calls, permissions, or external actions.",
      "Do not invent acronym definitions when unsure.",
      "",
      `User: ${userText}`,
      "Assistant:",
    ].join("\n");
  },
} as const;
