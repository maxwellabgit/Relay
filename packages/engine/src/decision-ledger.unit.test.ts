import { describe, expect, it } from "vitest";
import { DecisionLedger, isKeptDecision, parseDecisionLog } from "./decision-ledger.js";

describe("decision ledger", () => {
  it("keeps the acronym gate and refuses a saved response", async () => {
    const ledger = new DecisionLedger(1, () => "2026-09-19T00:00:00.000Z");
    await ledger.recordUnknownLookup("MSRP");
    const view = ledger.view();
    expect(view.decisions.map((line) => line.code)).toEqual(["lookup.unknown"]);
    expect(view.decisions[0]?.detail).not.toMatch(/search online/i);
    expect(view.gate?.rows.find((row) => row.label === "Jev")?.value).toBe("not called");
    expect(view.gate?.rows.find((row) => row.label === "Usefulness Noul")?.value).toBe("0.70");
    expect(isKeptDecision({ sequence: 1, at: "t", code: "lookup.unknown", detail: "Search online for X" })).toBe(
      false,
    );
  });

  it("discards an empty session and counts a session that did work", async () => {
    const ledger = new DecisionLedger(1, () => "2026-09-19T00:00:00.000Z");
    await ledger.startSession();
    expect(await ledger.endSession()).toBe("discarded");
    expect(ledger.view().expansion.completeSessions).toBe(0);
    expect(ledger.view().decisions).toEqual([]);

    await ledger.startSession();
    await ledger.recordExactLookup("API");
    expect(await ledger.endSession()).toBe("completed");
    const view = ledger.view();
    expect(view.expansion.completeSessions).toBe(1);
    expect(view.expansion.reflexesBuilt).toBe(1);
    expect(view.expansion.reviewDue).toBe(false);
    expect(view.gate?.rows.find((row) => row.label === "Result")?.value).toBe("bypassed");
  });

  it("raises one glossary-tool candidate after repeated unknown lookups", async () => {
    const ledger = new DecisionLedger(1, () => "2026-09-19T00:00:00.000Z");
    await ledger.recordUnknownLookup("MSRP");
    await ledger.recordUnknownLookup("BESS");
    expect(ledger.view().recommendations).toHaveLength(0);
    await ledger.recordUnknownLookup("OEM");
    const view = ledger.view();
    expect(view.recommendations).toEqual([
      {
        code: "glossary_tool",
        because: "repeated_unknown_lookup",
        count: 3,
        status: "candidate",
      },
    ]);
    expect(view.decisions.filter((line) => line.code === "pattern.candidate")).toHaveLength(1);
  });

  it("restores the last gate from a kept log without appending again", async () => {
    const appended: string[] = [];
    const ledger = new DecisionLedger(1, () => "2026-09-19T00:00:00.000Z", {
      directoryLabel: ".dev-data/dev-console/decisions.jsonl",
      append: async (record) => {
        appended.push(record.detail);
      },
      read: async () => [],
      replace: async () => {},
    });
    await ledger.hydrate([
      {
        sequence: 4,
        at: "2026-09-19T00:00:00.000Z",
        code: "gate.remember",
        detail: "MSRP · noul 0.82 · threshold 0.70 · pass",
      },
    ]);
    expect(ledger.view().gate?.title).toBe("Remember · MSRP");
    expect(appended).toEqual([]);
  });

  it("drops trash lines when reading a log", () => {
    const kept = parseDecisionLog(
      [
        JSON.stringify({ sequence: 1, at: "t", code: "lookup.exact", detail: "API · glossary · Jev bypassed" }),
        JSON.stringify({ sequence: 2, at: "t", code: "noise", detail: "hello", text: "What does API mean?" }),
        "not json",
      ].join("\n"),
    );
    expect(kept).toHaveLength(1);
  });
});
