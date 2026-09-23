import { describe, expect, it } from "vitest";
import { WorkSignal } from "./work-signal.js";

describe("work signal", () => {
  it("ends an idle wait when work is queued", async () => {
    const signal = new WorkSignal();
    const parent = new AbortController();
    const started = Date.now();
    const waiting = signal.idleWait(5_000, parent.signal);
    signal.kick();
    await waiting;
    expect(Date.now() - started).toBeLessThan(500);
    parent.abort();
  });
});
