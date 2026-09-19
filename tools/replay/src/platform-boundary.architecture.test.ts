import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

const forbidden = [
  "node:",
  "from \"fs\"",
  "from 'fs'",
  "from \"path\"",
  "from 'path'",
  "from \"react\"",
  "from 'react'",
  "from \"react-native\"",
  "from 'react-native'",
  "from \"expo\"",
  "from 'expo'",
  "@tauri-apps",
  "better-sqlite3",
  "WebBluetooth",
  "brilliant-ble",
];

function walk(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === "dist" || entry === "node_modules") continue;
      out.push(...walk(full));
    } else if (entry.endsWith(".ts") && !entry.endsWith(".test.ts")) {
      out.push(full);
    }
  }
  return out;
}

describe("platform boundary", () => {
  it("keeps engine and contracts free of platform imports", () => {
    const files = [
      ...walk(resolve(root, "packages/engine/src")),
      ...walk(resolve(root, "packages/contracts/src")),
    ];
    const violations: string[] = [];
    for (const file of files) {
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
