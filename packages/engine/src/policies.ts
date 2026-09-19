/** Engine-side policies that are not Reflex-specific. */

export function directAskPriority(): number {
  return 100;
}

export function observedCasePriority(): number {
  return 50;
}

export function shouldCreateCaseForFinal(origin: string, isAsk: boolean): {
  origin: "direct" | "observed";
  kind: "answer" | "resolve";
  priority: number;
} {
  if (isAsk) {
    return { origin: "direct", kind: "answer", priority: directAskPriority() };
  }
  return { origin: "observed", kind: "resolve", priority: observedCasePriority() };
}

const DEFINITION_STOP = new Set([
  "THE",
  "AND",
  "FOR",
  "WHAT",
  "DOES",
  "MEAN",
  "THIS",
  "THAT",
  "WITH",
  "FROM",
]);

/** Direct Ask with no local expansion becomes one recommended search task. */
export function definitionSearchTask(text: string): string | null {
  const patterns = [
    /\bwhat\s+(?:does|is|are)\s+([A-Za-z]{2,12})\b/i,
    /\bdefine\s+([A-Za-z]{2,12})\b/i,
    /\bmeaning\s+of\s+([A-Za-z]{2,12})\b/i,
  ];
  for (const pattern of patterns) {
    const raw = pattern.exec(text)?.[1];
    if (!raw) continue;
    const token = raw.toUpperCase();
    if (token.length < 2 || DEFINITION_STOP.has(token)) continue;
    return `Search online for the definition of ${token}`;
  }
  return null;
}

/** Remember only when Jev's yes probability clears this gate. */
export const REMEMBER_YES_MINIMUM = 0.7;

export type NoulInterval = {
  readonly probabilityYes: number;
  readonly probabilityNo: number;
  /** TypeSafe derived confidence: max(P(yes), P(no)). Never below 0.5. */
  readonly confidence: number;
  readonly low: number;
  readonly high: number;
  readonly accept: boolean;
};

/**
 * The confidence interval for a Noul is the yes/no split.
 * Acceptance uses P(yes), not the lower bound, because that bound is always ≤ 0.5.
 */
export function noulConfidenceInterval(probabilityYes: number): NoulInterval | null {
  if (!Number.isFinite(probabilityYes) || probabilityYes < 0 || probabilityYes > 1) return null;
  const probabilityNo = 1 - probabilityYes;
  const low = Math.min(probabilityYes, probabilityNo);
  const high = Math.max(probabilityYes, probabilityNo);
  return {
    probabilityYes,
    probabilityNo,
    confidence: high,
    low,
    high,
    accept: probabilityYes >= REMEMBER_YES_MINIMUM,
  };
}

export function formatNoulInterval(interval: NoulInterval): string {
  const yes = interval.probabilityYes.toFixed(2);
  const low = interval.low.toFixed(2);
  const high = interval.high.toFixed(2);
  return `P(yes)=${yes} · confidence interval ${low}–${high}`;
}
