const CALENDAR_SENTENCE =
  "Observed 3 completed calendar-block episodes across 3 sessions: 60 minutes near noon, reminder one day before. Recommend creating a Calendar Block Reflex?";

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

export const calendarBlockEpisode: EpisodeDefinition = {
  kind: "calendar.block",
  eligibleOutcomes: ["completed"],
  candidateTemplateId: "calendar.block",
  normalize(fields) {
    return signature("calendar.block", fields);
  },
  render(pattern) {
    if (pattern.count < 3 || pattern.sessions < 2) return null;
    if (pattern.count === 3 && pattern.sessions === 3) return CALENDAR_SENTENCE;
    return `Observed ${pattern.count} completed calendar-block episodes across ${pattern.sessions} sessions: 60 minutes near noon, reminder one day before. Recommend creating a Calendar Block Reflex?`;
  },
};

function signature(kind: string, fields: Readonly<Record<string, string>>): string | null {
  const keys = Object.keys(fields).sort();
  for (const key of keys) {
    const value = fields[key];
    if (!/^[a-z0-9_]{1,24}$/.test(key)) return null;
    if (typeof value !== "string" || !/^[A-Za-z0-9._+-]{1,32}$/.test(value)) return null;
  }
  return [kind, ...keys.map((key) => `${key}=${fields[key]}`)].join("|");
}
