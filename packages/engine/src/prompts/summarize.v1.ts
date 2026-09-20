export const SUMMARIZE_PROMPT_V1 = {
  taskKind: "summarize",
  promptVersion: "summarize.v1",
  build(userText: string): string {
    return [
      "Summarize the following text in one or two short sentences.",
      "Do not add facts that are not present.",
      "",
      userText,
      "",
      "Summary:",
    ].join("\n");
  },
} as const;
