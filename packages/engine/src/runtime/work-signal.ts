import { sleep } from "../engine-helpers.js";

/**
 * Wakes the work loop as soon as a job is queued.
 * The idle sleep is not a second scheduler.
 */
export class WorkSignal {
  private idle: AbortController | null = null;

  kick(): void {
    this.idle?.abort();
  }

  async idleWait(ms: number, signal: AbortSignal): Promise<void> {
    if (signal.aborted) return;
    const idle = new AbortController();
    this.idle = idle;
    const stop = () => idle.abort();
    signal.addEventListener("abort", stop, { once: true });
    try {
      await sleep(ms, idle.signal);
    } finally {
      signal.removeEventListener("abort", stop);
      if (this.idle === idle) this.idle = null;
    }
  }
}
