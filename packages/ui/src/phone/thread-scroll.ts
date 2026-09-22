/** Follow the thread only when the reader is already near the end. */

export const THREAD_FOLLOW_PX = 80;

export type ThreadScroll = {
  readonly pinned: boolean;
  readonly offset: number;
};

export type ThreadScrollHandle = {
  current: ThreadScroll;
};

export function nextThreadScroll(
  measurement: {
    readonly contentHeight: number;
    readonly viewportHeight: number;
    readonly offsetY: number;
  },
  followPx = THREAD_FOLLOW_PX,
): ThreadScroll {
  const offset = Number.isFinite(measurement.offsetY) ? Math.max(0, measurement.offsetY) : 0;
  const distance = measurement.contentHeight - measurement.viewportHeight - offset;
  return { pinned: distance < followPx, offset };
}

/** Where to place the thread after the list mounts again. */
export function threadRestoreTarget(
  saved: ThreadScroll,
): { readonly kind: "end" } | { readonly kind: "offset"; readonly offset: number } {
  if (saved.pinned) return { kind: "end" };
  return { kind: "offset", offset: Math.max(0, saved.offset) };
}
