export function acronymProviderState(input: {
  readonly token: string;
  readonly contextExcerpt: string;
  readonly optionCount: number;
  readonly explicitAsk: boolean;
  readonly sourceEventId: string;
}): Record<string, unknown> {
  const contextExcerpt = input.contextExcerpt.trim().slice(0, 400);
  return {
    token: input.token,
    ...(contextExcerpt ? { contextExcerpt } : {}),
    optionCount: input.optionCount,
    explicitAsk: input.explicitAsk,
    provenance: "glossary_window",
    policyVersion: "resolve-acronym@1",
    sourceRef: input.sourceEventId,
  };
}
