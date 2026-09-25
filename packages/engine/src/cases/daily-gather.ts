/** One read-only gather per local day, somewhere from 09:00 through 16:59. */
const WINDOW_START_MINUTE = 9 * 60;
const WINDOW_MINUTES = 8 * 60;

export function localDateKey(at: Date): string {
  const month = String(at.getMonth() + 1).padStart(2, "0");
  const day = String(at.getDate()).padStart(2, "0");
  return `${at.getFullYear()}-${month}-${day}`;
}

/** Stable for a calendar day so a restart does not pick a second time. */
export function dailyReadMinute(localDate: string): number {
  let hash = 2166136261;
  for (const char of localDate) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619);
  return WINDOW_START_MINUTE + ((hash >>> 0) % WINDOW_MINUTES);
}

export function gatherIsDue(at: Date, minuteOfDay: number): boolean {
  return at.getHours() * 60 + at.getMinutes() >= minuteOfDay;
}
