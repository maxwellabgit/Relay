import { describe, expect, it } from "vitest";
import { definitionSearchTask, formatNoulInterval, noulConfidenceInterval } from "./policies.js";

describe("definitionSearchTask", () => {
  it("recommends an online search for an unknown acronym ask", () => {
    expect(definitionSearchTask("What does MSRP mean?")).toBe(
      "Search online for the definition of MSRP",
    );
  });

  it("does not invent a task for ordinary text", () => {
    expect(definitionSearchTask("BESS glossary check")).toBeNull();
  });

  it("accepts memory only when the Jev yes probability clears 0.70", () => {
    const high = noulConfidenceInterval(0.82);
    const low = noulConfidenceInterval(0.41);
    expect(high?.accept).toBe(true);
    expect(high?.low).toBeCloseTo(0.18);
    expect(high?.high).toBeCloseTo(0.82);
    expect(formatNoulInterval(high!)).toContain("confidence interval 0.18–0.82");
    expect(low?.accept).toBe(false);
    expect(noulConfidenceInterval(1.2)).toBeNull();
  });
});
