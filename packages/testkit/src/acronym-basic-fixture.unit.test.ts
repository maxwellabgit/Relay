import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { ACRONYM_BASIC_EVENTS, parseTranscriptJsonl } from "./acronym-basic-fixture.js";

describe("acronym-basic fixture parity", () => {
  it("matches the public JSONL fixture byte-for-byte after parse", () => {
    const path = resolve(process.cwd(), "fixtures/public/transcripts/acronym-basic.jsonl");
    const fromDisk = parseTranscriptJsonl(readFileSync(path, "utf8"));
    expect(fromDisk).toEqual(ACRONYM_BASIC_EVENTS);
  });
});
