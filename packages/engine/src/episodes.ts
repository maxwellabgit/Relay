export type EpisodePatternCounts = {
  readonly count: number;
  readonly sessions: number;
};

export type EpisodeDefinition = {
  readonly kind: string;
  normalize(fields: Readonly<Record<string, string>>): string | null;
  readonly eligibleOutcomes: readonly ["completed"];
  readonly candidateTemplateId?: string;
  render?(pattern: EpisodePatternCounts): string | null;
};
