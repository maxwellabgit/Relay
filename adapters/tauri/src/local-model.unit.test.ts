import { describe, expect, it, vi } from "vitest";
import { TauriLocalModelPort } from "./local-model.js";

describe("TauriLocalModelPort", () => {
  it("maps successful generate responses", async () => {
    const invoke = vi.fn(async (command: string) => {
      if (command === "local_model_generate") {
        return { ok: true, text: "TCP is connection-oriented.", model: "local", elapsed_ms: 12 };
      }
      throw new Error(command);
    });
    const port = new TauriLocalModelPort(invoke);
    const result = await port.generate(
      { taskKind: "direct_answer", promptVersion: "direct-answer.v1", prompt: "q" },
      new AbortController().signal,
    );
    expect(result).toEqual({
      ok: true,
      text: "TCP is connection-oriented.",
      model: "local",
      elapsedMs: 12,
    });
  });

  it("maps unavailable, timeout, invalid, and cancelled failures", async () => {
    const port = new TauriLocalModelPort(async () => ({
      ok: false,
      failure_reason: "timeout",
      elapsed_ms: 1,
    }));
    await expect(
      port.generate({ taskKind: "direct_answer", prompt: "q" }, new AbortController().signal),
    ).resolves.toEqual({ ok: false, failureReason: "timeout" });

    const aborted = new AbortController();
    aborted.abort();
    await expect(
      port.generate({ taskKind: "direct_answer", prompt: "q" }, aborted.signal),
    ).resolves.toEqual({ ok: false, failureReason: "cancelled" });

    const invalid = new TauriLocalModelPort(async () => ({ ok: true, text: "  ", model: "local", elapsed_ms: 1 }));
    await expect(
      invalid.generate({ taskKind: "direct_answer", prompt: "q" }, new AbortController().signal),
    ).resolves.toEqual({ ok: false, failureReason: "invalid_response" });
  });

  it("reports status ready only when native status is ok", async () => {
    const ready = new TauriLocalModelPort(async () => ({ ok: true, detail: "ready", model: "m" }));
    await expect(ready.status()).resolves.toEqual({ ok: true, detail: "ready", model: "m" });
    const down = new TauriLocalModelPort(async () => ({ ok: false, detail: "unavailable", model: null }));
    await expect(down.status()).resolves.toEqual({ ok: false, detail: "unavailable", model: null });
  });
});
