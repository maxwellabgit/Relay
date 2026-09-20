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

export function validateBirthday(displayName: string, date: string): string | null {
  if (!isPersonName(displayName)) return "invalid_person";
  if (!parseFlexibleDate(date)) return "invalid_date";
  return null;
}

export function isPersonName(value: string): boolean {
  const name = value.normalize("NFKC").trim();
  if (name.length < 1 || name.length > 80) return false;
  return /^[\p{L}\p{M}][\p{L}\p{M}\p{N} .'’-]{0,79}$/u.test(name);
}

export function parseFlexibleDate(
  value: string,
): { readonly month: number; readonly day: number; readonly year?: number } | null {
  const text = value.trim().replace(/\.$/, "");
  const iso = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (iso) {
    const year = Number(iso[1]);
    const month = Number(iso[2]);
    const day = Number(iso[3]);
    return realDate(year, month, day) ? { month, day, year } : null;
  }
  const monthDay = /^(\d{2})-(\d{2})$/.exec(text);
  if (monthDay) {
    const month = Number(monthDay[1]);
    const day = Number(monthDay[2]);
    return realDate(2024, month, day) ? { month, day } : null;
  }
  const named = /^([A-Za-z]+)\s+(\d{1,2})(?:,\s*(\d{4}))?$/.exec(text);
  if (!named) return null;
  const month = MONTHS[named[1]!.toLowerCase()];
  const day = Number(named[2]);
  const year = named[3] ? Number(named[3]) : undefined;
  if (!month || !realDate(year ?? 2024, month, day)) return null;
  return year ? { month, day, year } : { month, day };
}

export function parseBirthdayUtterance(
  text: string,
): { readonly displayName: string; readonly month: number; readonly day: number; readonly year?: number } | null {
  const match = /^remember that (.+?)['’]s birthday is (.+)$/i.exec(text.trim());
  if (!match) return null;
  const displayName = match[1]!.trim();
  if (!isPersonName(displayName)) return null;
  const date = parseFlexibleDate(match[2]!);
  if (!date) return null;
  return { displayName, ...date };
}

export function parseGlossaryMeans(text: string): { readonly token: string; readonly expansion: string } | null {
  const match = /^([A-Za-z0-9]{2,12})\s+means\s+(.+)$/i.exec(text.trim());
  if (!match) return null;
  const token = match[1]!.toUpperCase();
  const expansion = match[2]!.trim().replace(/[.]+$/, "");
  if (!/^[A-Z0-9]{2,12}$/.test(token) || expansion.length < 2 || expansion.length > 120) return null;
  return { token, expansion };
}

const MONTHS: Readonly<Record<string, number>> = {
  january: 1,
  february: 2,
  march: 3,
  april: 4,
  may: 5,
  june: 6,
  july: 7,
  august: 8,
  september: 9,
  october: 10,
  november: 11,
  december: 12,
};

function realDate(year: number, month: number, day: number): boolean {
  const utc = new Date(Date.UTC(year, month - 1, day));
  return utc.getUTCFullYear() === year && utc.getUTCMonth() === month - 1 && utc.getUTCDate() === day;
}

export function validateChoiceDistribution(input: {
  readonly probabilities: Readonly<Record<string, number>>;
  readonly declared: string;
  readonly allowed: readonly string[];
}): { readonly ok: true } | { readonly ok: false; readonly reasonCode: string } {
  if (!input.declared) return { ok: false, reasonCode: "missing_choice" };
  const allowed = new Set([...input.allowed, "no_match"]);
  if (!allowed.has(input.declared)) return { ok: false, reasonCode: "not_in_options" };
  const keys = Object.keys(input.probabilities);
  if (keys.length === 0) return { ok: false, reasonCode: "missing_choice" };
  let sum = 0;
  for (const key of keys) {
    if (!allowed.has(key)) return { ok: false, reasonCode: "not_in_options" };
    const probability = input.probabilities[key]!;
    if (!Number.isFinite(probability) || probability < 0 || probability > 1) {
      return { ok: false, reasonCode: "invalid_probability" };
    }
    sum += probability;
  }
  if (Math.abs(sum - 1) > 0.05) return { ok: false, reasonCode: "distribution_sum" };
  const top = [...keys].sort((left, right) => input.probabilities[right]! - input.probabilities[left]!)[0];
  if (top !== input.declared) return { ok: false, reasonCode: "choice_conflict" };
  return { ok: true };
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
