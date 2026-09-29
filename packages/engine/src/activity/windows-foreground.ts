export type ForegroundSample = {
  readonly atMs: number;
  readonly processName: string;
  readonly windowTitle: string;
};

export type ForegroundDecision =
  | { readonly emit: false; readonly reason: "duplicate" | "empty" }
  | { readonly emit: true; readonly previousDurationMs: number | null; readonly sample: ForegroundSample };

/**
 * Same dedupe rule as the Windows observer: emit on process or title change,
 * and carry the previous focus duration. Identical samples are dropped.
 */
export function nextForegroundEvent(previous: ForegroundSample | null, sample: ForegroundSample): ForegroundDecision {
  const processName = sample.processName.trim();
  const windowTitle = sample.windowTitle.trim();
  if (!processName && !windowTitle) return { emit: false, reason: "empty" };
  if (
    previous &&
    previous.processName === processName &&
    previous.windowTitle === windowTitle
  ) {
    return { emit: false, reason: "duplicate" };
  }
  const previousDurationMs =
    previous && sample.atMs >= previous.atMs ? sample.atMs - previous.atMs : null;
  return {
    emit: true,
    previousDurationMs,
    sample: { atMs: sample.atMs, processName, windowTitle },
  };
}
