import { describe, expect, it } from "vitest";
import { memoryLibraryLabel } from "./memory-label.js";

describe("memory library label", () => {
  it("shows polished prose and hides the internal key", () => {
    expect(
      memoryLibraryLabel({
        kind: "note",
        key: "note:buy filters",
        fields: { text: "buy filters", recordType: "note", status: "accepted" },
      }),
    ).toBe("Note: buy filters");
    expect(
      memoryLibraryLabel({
        kind: "fact",
        key: "fact:MSRP means list price",
        fields: { text: "MSRP means list price" },
      }),
    ).toBe("Fact: MSRP means list price");
    expect(
      memoryLibraryLabel({
        kind: "recommendation",
        key: "recommendation:call the supplier",
        fields: { text: "call the supplier" },
      }),
    ).toBe("Next: call the supplier");
  });

  it("does not fall back to the storage key when prose is empty", () => {
    expect(
      memoryLibraryLabel({
        kind: "recommendation",
        key: "recommendation:open",
        fields: { recordType: "recommendation", status: "accepted" },
      }),
    ).toBe("Next");
  });
});
