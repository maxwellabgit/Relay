import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

function walk(dir: string, predicate: (name: string) => boolean): string[] {
  if (!existsSync(dir)) return [];
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === "dist" || entry === "node_modules" || entry === "dist-types") continue;
      out.push(...walk(full, predicate));
    } else if (predicate(entry)) {
      out.push(full);
    }
  }
  return out;
}

describe("retired architecture absence", () => {
  it("does not keep the retired C# tree on the active branch", () => {
    expect(existsSync(resolve(root, "src"))).toBe(false);
    expect(existsSync(resolve(root, "tests"))).toBe(false);
    expect(existsSync(resolve(root, "Relay.slnx"))).toBe(false);
    expect(existsSync(resolve(root, "Directory.Build.props"))).toBe(false);
  });

  it("keeps an archive pointer to the preservation tag", () => {
    const text = readFileSync(resolve(root, "docs/archive/DOTNET_BASELINE.md"), "utf8");
    expect(text).toContain("relay-dotnet-a6bf987");
  });

  it("has no active instruction to build or run the retired .NET stack", () => {
    const activeDocs = [
      resolve(root, "README.md"),
      resolve(root, "docs/STATUS.md"),
      resolve(root, "docs/WINDOWS_V1_ACCEPTANCE.md"),
      resolve(root, "docs/architecture/ADR-001-cross-platform-runtime.md"),
      resolve(root, "dev/README.md"),
      resolve(root, "dev/run-relay.ps1"),
    ];
    const forbidden = [
      "dotnet build Relay.slnx",
      "dotnet run --project src/Relay",
      "run-dotnet-baseline",
      "run-harness.ps1",
      "Relay.Desktop",
      "WinUI shell",
    ];
    const violations: string[] = [];
    for (const file of activeDocs) {
      const text = readFileSync(file, "utf8");
      for (const needle of forbidden) {
        if (text.includes(needle)) {
          violations.push(`${relative(root, file)} contains ${needle}`);
        }
      }
    }
    expect(violations).toEqual([]);
  });
});

describe("package import boundaries", () => {
  it("keeps packages/ui importing product types from @relay/contracts only", () => {
    const files = walk(
      resolve(root, "packages/ui/src"),
      (name) => name.endsWith(".ts") || name.endsWith(".tsx"),
    );
    const violations: string[] = [];
    const forbidden = ["@relay/engine", "@relay/adapter-", "@tauri-apps", "better-sqlite3"];
    for (const file of files) {
      if (file.includes(".test.")) continue;
      const text = readFileSync(file, "utf8");
      for (const needle of forbidden) {
        if (text.includes(needle)) {
          violations.push(`${relative(root, file)} contains ${needle}`);
        }
      }
    }
    expect(violations).toEqual([]);
  });

  it("keeps packages/engine free of React, Tauri, and provider SDKs", () => {
    const files = walk(resolve(root, "packages/engine/src"), (name) => name.endsWith(".ts"));
    const violations: string[] = [];
    const forbidden = [
      'from "react"',
      "from 'react'",
      "react-native",
      "@tauri-apps",
      "openai",
      "@anthropic",
      "better-sqlite3",
    ];
    for (const file of files) {
      if (file.includes(".test.")) continue;
      const text = readFileSync(file, "utf8");
      for (const needle of forbidden) {
        if (text.includes(needle)) {
          violations.push(`${relative(root, file)} contains ${needle}`);
        }
      }
    }
    expect(violations).toEqual([]);
  });
});
