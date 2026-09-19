import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

describe("workspace bootstrap", () => {
  it("declares the required root scripts", () => {
    const pkg = JSON.parse(readFileSync(resolve(root, "package.json"), "utf8")) as {
      scripts: Record<string, string>;
    };
    for (const script of [
      "dev:web",
      "dev:desktop",
      "build:web",
      "build:desktop",
      "check",
      "test:unit",
      "test:integration",
      "test:replay",
      "test:privacy",
      "audio:prepare",
      "halo:emulator",
    ]) {
      expect(pkg.scripts[script], script).toBeTypeOf("string");
    }
  });

  it("covers apps, packages, adapters, and tools workspaces", () => {
    const pkg = JSON.parse(readFileSync(resolve(root, "package.json"), "utf8")) as {
      workspaces: string[];
    };
    expect(pkg.workspaces).toEqual(
      expect.arrayContaining(["apps/*", "packages/*", "adapters/*", "tools/*"]),
    );
  });
});
