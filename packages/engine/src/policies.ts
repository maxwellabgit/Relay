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

/** The yes/no split of a Noul. This is not a statistical confidence interval. */
export function formatNoulInterval(interval: NoulInterval): string {
  const yes = interval.probabilityYes.toFixed(2);
  const no = interval.probabilityNo.toFixed(2);
  return `P(yes)=${yes} · P(no)=${no}`;
}

export function askedToken(text: string): string | null {
  const task = definitionSearchTask(text);
  if (!task) return null;
  return /([A-Z0-9]{2,12})$/.exec(task)?.[1] ?? null;
}

export function validateBirthday(personKey: string, date: string): string | null {
  if (!/^[A-Za-z][A-Za-z0-9]{0,24}$/.test(personKey)) return "invalid_person";
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!match) return "invalid_date";
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const utc = new Date(Date.UTC(year, month - 1, day));
  if (utc.getUTCFullYear() !== year || utc.getUTCMonth() !== month - 1 || utc.getUTCDate() !== day) {
    return "invalid_date";
  }
  return null;
}

export function evaluateChoiceGate(input: {
  readonly probabilities: Readonly<Record<string, number>>;
  readonly minimum: number;
  readonly marginMinimum: number;
}): { readonly pass: boolean; readonly top: number; readonly margin: number; readonly selected: string; readonly reasonCode: string } {
  const ranked = Object.entries(input.probabilities).sort((a, b) => b[1] - a[1]);
  const top = ranked[0];
  const second = ranked[1];
  if (!top) return { pass: false, top: 0, margin: 0, selected: "", reasonCode: "missing_choice" };
  const margin = second ? top[1] - second[1] : top[1];
  if (top[1] < input.minimum) {
    return { pass: false, top: top[1], margin, selected: top[0], reasonCode: "below_choice_minimum" };
  }
  if (second && margin < input.marginMinimum) {
    return { pass: false, top: top[1], margin, selected: top[0], reasonCode: "below_choice_margin" };
  }
  return { pass: true, top: top[1], margin, selected: top[0], reasonCode: "policy_pass" };
}
