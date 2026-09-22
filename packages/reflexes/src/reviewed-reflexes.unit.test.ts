import { describe, expect, it } from "vitest";
import { captureNoteModule } from "./capture-note/module.js";
import { recommendNextActionModule } from "./recommend-next-action/module.js";
import { rememberFactModule } from "./remember-fact/module.js";
import { createProductionReflexes } from "./registry.js";

const event = {
  sourceEventId: "s1",
  segmentId: "s1",
  origin: "typed",
  speakerKey: null,
  startMs: 0,
  endMs: 0,
};

const detection = { sessionId: "sess", now: "2026-09-22T00:00:00.000Z", listening: false };

describe("reviewed V1 reflex modules", () => {
  it("registers acronym, note, fact, and next-action", () => {
    const ids = createProductionReflexes({
      async getMemory() {
        return null;
      },
    }).map((module) => module.definition.id);
    expect(ids).toEqual([
      "reflex.resolve-acronym",
      "reflex.capture-note",
      "reflex.remember-fact",
      "reflex.recommend-next-action",
    ]);
  });

  it("captures an explicit note and ignores chatter", async () => {
    expect(
      captureNoteModule.detect({ ...event, text: "hello there" }, detection),
    ).toEqual([]);
    const [trigger] = captureNoteModule.detect(
      { ...event, text: "note: buy filters" },
      detection,
    );
    expect(trigger?.token).toBe("buy filters");
    const result = await captureNoteModule.evaluate({
      caseId: "c1",
      caseVersion: 1,
      reflex: { id: captureNoteModule.definition.id, version: 1 },
      triggerSourceRefs: [],
      eligibleConnections: [],
      remainingBudgets: captureNoteModule.definition.budgets,
      now: detection.now,
      triggerToken: trigger?.token ?? "",
    });
    expect(result.type).toBe("finding");
    if (result.type === "finding") expect(result.summary).toBe("note:buy filters");
  });

  it("remembers an explicit fact", () => {
    const [trigger] = rememberFactModule.detect(
      { ...event, text: "remember that MSRP means list price" },
      detection,
    );
    expect(trigger?.token).toBe("MSRP means list price");
  });

  it("recommends only from an explicit next-action phrase", () => {
    expect(
      recommendNextActionModule.detect({ ...event, text: "maybe later" }, detection),
    ).toEqual([]);
    const [trigger] = recommendNextActionModule.detect(
      { ...event, text: "next action: call the supplier" },
      detection,
    );
    expect(trigger?.token).toBe("call the supplier");
  });
});
