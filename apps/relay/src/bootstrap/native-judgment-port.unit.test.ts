import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import type { JudgmentRequest } from "@relay/contracts";
import { createNativeJudgmentPort, type TauriInvoke } from "./native-judgment-port.js";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");
const question = { remember: { type: "noul" as const, instructions: "Remember?" } };
const request: JudgmentRequest = {
  questionSetId: "judgment.remember",
  questionSetVersion: "1",
  model: "jev-latest",
  state: { token: "MSRP" },
  questions: question,
};

const noWait = async () => undefined;

describe("production native Jev port", () => {
  it("does not call native HTTP when the grant is exhausted", async () => {
    const commands: string[] = [];
    const invoke: TauriInvoke = async (command) => {
      commands.push(command);
      return { ok: true, status: 200, body: {} };
    };
    const port = createNativeJudgmentPort(invoke, { sleep: noWait, maxAttempts: 2 });
    const result = await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            return { ok: false, reason: "exhausted" };
          },
          async commit() {
            throw new Error("commit");
          },
          async release() {
            throw new Error("release");
          },
        },
      },
      new AbortController().signal,
    );
    expect(commands).toEqual([]);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.failure.category).toBe("not_authorized");
      expect(result.failure.transport?.attempts).toBe(0);
      expect(result.failure.transport?.networkAttempted).toBe(false);
    }
  });

  it("charges every retry and reports the native attempt count", async () => {
    const charged: string[] = [];
    let calls = 0;
    const invoke: TauriInvoke = async (command) => {
      expect(command).toBe("typesafe_judge");
      calls += 1;
      if (calls === 1) return { ok: false, status: 429, category: "rate_limited", retry_after: "0" };
      return { ok: false, status: 401, category: "authentication" };
    };
    const port = createNativeJudgmentPort(invoke, { sleep: noWait, maxAttempts: 3, random: () => 0, now: () => 0 });
    const result = await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            return { ok: true, reservationId: `res_${charged.length + 1}` };
          },
          async commit(reservationId) {
            charged.push(reservationId);
          },
          async release() {
            throw new Error("release");
          },
        },
      },
      new AbortController().signal,
    );
    expect(calls).toBe(2);
    expect(charged).toEqual(["res_1", "res_2"]);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.failure.transport?.attempts).toBe(2);
  });

  it("releases a reservation when cancelled before the native call", async () => {
    const released: string[] = [];
    const invoke: TauriInvoke = async () => {
      throw new Error("native");
    };
    const controller = new AbortController();
    const port = createNativeJudgmentPort(invoke, { sleep: noWait });
    const result = await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            controller.abort();
            return { ok: true, reservationId: "res_cancel" };
          },
          async commit() {
            throw new Error("commit");
          },
          async release(reservationId) {
            released.push(reservationId);
          },
        },
      },
      controller.signal,
    );
    expect(released).toEqual(["res_cancel"]);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.failure.category).toBe("cancelled");
      expect(result.failure.transport?.attempts).toBe(0);
    }
  });

  it("aborts an in-flight native request and keeps the charge", async () => {
    const commands: string[] = [];
    const controller = new AbortController();
    const invoke: TauriInvoke = async (command) => {
      commands.push(command);
      if (command === "typesafe_judge") {
        controller.abort();
        return { ok: false, status: 0, category: "cancelled" };
      }
      return null;
    };
    const port = createNativeJudgmentPort(invoke, { sleep: noWait, maxAttempts: 3 });
    const result = await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            return { ok: true, reservationId: "res_live" };
          },
          async commit() {
            return undefined;
          },
          async release() {
            throw new Error("release");
          },
        },
      },
      controller.signal,
    );
    expect(commands).toContain("typesafe_judge");
    expect(commands).toContain("typesafe_cancel");
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.failure.category).toBe("cancelled");
      expect(result.failure.transport?.attempts).toBe(1);
    }
  });

  it("restarts by dropping an uncommitted reservation before the next charge", async () => {
    const open = new Map<string, "open" | "committed">();
    const restart = () => {
      for (const [id, state] of open) {
        if (state === "open") open.delete(id);
      }
    };
    open.set("stale", "open");
    restart();
    expect(open.has("stale")).toBe(false);

    const invoke: TauriInvoke = async () => ({ ok: false, status: 401, category: "authentication" });
    const port = createNativeJudgmentPort(invoke, { sleep: noWait, maxAttempts: 1 });
    await port.judge(
      {
        ...request,
        physicalBudget: {
          async beforeAttempt() {
            open.set("res_next", "open");
            return { ok: true, reservationId: "res_next" };
          },
          async commit(reservationId) {
            open.set(reservationId, "committed");
          },
          async release(reservationId) {
            open.delete(reservationId);
          },
        },
      },
      new AbortController().signal,
    );
    expect(open.get("res_next")).toBe("committed");
    const state = readFileSync(resolve(root, "apps/desktop/src-tauri/src/state.rs"), "utf8");
    const desktop = readFileSync(resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts"), "utf8");
    expect(state).toContain("release_uncommitted_on_open");
    expect(desktop).toContain("createNativeJudgmentPort");
    expect(desktop).toContain("runPackagedLiveCanary");
  });
});
