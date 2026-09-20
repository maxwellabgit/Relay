import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");

describe("windows production composition", () => {
  it("does not import MemoryArtifactStore in createDesktopClient", () => {
    const path = resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts");
    const text = readFileSync(path, "utf8");
    expect(text).not.toMatch(/MemoryArtifactStore/);
    expect(text).toMatch(/TauriArtifactStore/);
  });
});
