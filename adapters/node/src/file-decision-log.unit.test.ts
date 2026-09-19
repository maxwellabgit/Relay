import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createFileDecisionLog } from "./file-decision-log.js";

describe("file decision log", () => {
  it("appends kept lines and drops trash on read", async () => {
    const directory = await mkdtemp(join(tmpdir(), "relay-log-"));
    const log = createFileDecisionLog(directory);
    await log.append({
      sequence: 1,
      at: "2026-09-19T00:00:00.000Z",
      code: "lookup.unknown",
      detail: "MSRP · no candidates · Jev not called",
    });
    await log.append({
      sequence: 2,
      at: "2026-09-19T00:00:01.000Z",
      code: "lookup.unknown",
      detail: "Search online for the definition of MSRP",
    });

    const text = await readFile(join(directory, "decisions.jsonl"), "utf8");
    expect(text).not.toContain("Search online");
    const kept = await log.read();
    expect(kept).toHaveLength(1);
    expect(kept[0]?.detail).toBe("MSRP · no candidates · Jev not called");

    await log.replace([]);
    expect(await log.read()).toEqual([]);
  });
});
