import { execFile } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { describe, expect, it } from "vitest";

const execFileAsync = promisify(execFile);

const require = createRequire(import.meta.url);
const { redirectProductionModule } = require("../../apps/relay/metro-purity.cjs") as {
  redirectProductionModule: (
    moduleName: string,
    env: Record<string, string | undefined>,
  ) => string | null;
};

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const ENTRIES = [
  "apps/relay/index.js",
  "apps/relay/src/App.tsx",
  "apps/relay/src/bootstrap/createAppClient.ts",
  "apps/relay/src/bootstrap/createDesktopClient.ts",
  "apps/relay/src/bootstrap/createMobileClient.ts",
  "apps/relay/src/bootstrap/createMobileClient.native.ts",
];

type PackageInfo = {
  readonly dir: string;
  readonly exports: Record<string, string>;
};

function walkPackageJson(dir: string, out: Map<string, PackageInfo>): void {
  if (!existsSync(dir)) return;
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (!statSync(full).isDirectory()) continue;
    if (entry === "node_modules" || entry === "dist" || entry === "dist-types") continue;
    const manifestPath = join(full, "package.json");
    if (!existsSync(manifestPath)) {
      walkPackageJson(full, out);
      continue;
    }
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8")) as {
      name?: string;
      exports?: unknown;
    };
    if (manifest.name?.startsWith("@relay/")) {
      out.set(manifest.name, { dir: full, exports: flattenExports(manifest.exports) });
    }
  }
}

function flattenExports(value: unknown): Record<string, string> {
  if (!value || typeof value !== "object") return {};
  const out: Record<string, string> = {};
  for (const [key, target] of Object.entries(value as Record<string, unknown>)) {
    const picked = pickTarget(target);
    if (picked) out[key] = picked;
  }
  return out;
}

function pickTarget(target: unknown): string | null {
  if (typeof target === "string") return target;
  if (!target || typeof target !== "object") return null;
  const record = target as Record<string, unknown>;
  for (const key of ["import", "default", "require", "types"]) {
    if (typeof record[key] === "string") return record[key];
  }
  return null;
}

function resolveExisting(candidates: readonly string[]): string | null {
  for (const candidate of candidates) {
    if (existsSync(candidate) && statSync(candidate).isFile()) return candidate;
  }
  return null;
}

function resolveRelative(fromDir: string, spec: string): string | null {
  const base = resolve(fromDir, spec);
  const swapped = spec.endsWith(".js")
    ? [base.slice(0, -3) + ".ts", base.slice(0, -3) + ".tsx"]
    : [];
  return resolveExisting([
    base,
    ...swapped,
    `${base}.ts`,
    `${base}.tsx`,
    `${base}.js`,
    join(base, "index.ts"),
    join(base, "index.tsx"),
    join(base, "index.js"),
  ]);
}

function resolveWorkspace(spec: string, packages: Map<string, PackageInfo>): string | null {
  const names = [...packages.keys()].sort((a, b) => b.length - a.length);
  const name = names.find((candidate) => spec === candidate || spec.startsWith(`${candidate}/`));
  if (!name) return null;
  const info = packages.get(name);
  if (!info) return null;
  const sub = spec === name ? "." : `./${spec.slice(name.length + 1)}`;
  const target = info.exports[sub];
  if (!target) return null;
  return resolveRelative(info.dir, target);
}

function staticSpecifiers(source: string): string[] {
  const stripped = source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
  const specs: string[] = [];
  for (const match of stripped.matchAll(/\bfrom\s+["']([^"']+)["']/g)) {
    if (match[1]) specs.push(match[1]);
  }
  for (const match of stripped.matchAll(/\bimport\s+["']([^"']+)["']/g)) {
    if (match[1]) specs.push(match[1]);
  }
  return specs;
}

function productionGraph(): { files: string[]; unresolved: string[] } {
  const packages = new Map<string, PackageInfo>();
  for (const dir of ["packages", "adapters", "apps", "tools"]) {
    walkPackageJson(resolve(root, dir), packages);
  }
  const files: string[] = [];
  const unresolved: string[] = [];
  const seen = new Set<string>();
  const queue = ENTRIES.map((entry) => resolve(root, entry));
  while (queue.length > 0) {
    const file = queue.pop();
    if (!file || seen.has(file) || !existsSync(file)) continue;
    seen.add(file);
    files.push(file);
    const source = readFileSync(file, "utf8");
    for (const spec of staticSpecifiers(source)) {
      const resolved = spec.startsWith(".")
        ? resolveRelative(dirname(file), spec)
        : spec.startsWith("@relay/")
          ? resolveWorkspace(spec, packages)
          : null;
      if (spec.startsWith(".") || spec.startsWith("@relay/")) {
        if (!resolved) unresolved.push(`${relative(root, file)} -> ${spec}`);
        else queue.push(resolved);
      }
    }
  }
  return { files, unresolved };
}

describe("production metro redirects", () => {
  it("loads optional modules only on the internal channel with the matching flag", () => {
    const off = {};
    expect(redirectProductionModule("./createBrowserDemoClient.js", off)).toBe("./demo-blocked");
    expect(redirectProductionModule("./dev/replay-acronym-fixture.js", off)).toBe(
      "./dev/fixture-replay-blocked",
    );
    expect(redirectProductionModule("./demo-blocked", off)).toBeNull();
    const flagged = {
      EXPO_PUBLIC_RELAY_ALLOW_DEMO: "1",
      EXPO_PUBLIC_RELAY_DEV_CONSOLE: "true",
    };
    expect(redirectProductionModule("./createBrowserDemoClient.js", flagged)).toBe(
      "./demo-blocked",
    );
    expect(redirectProductionModule("./dev/replay-acronym-fixture.js", flagged)).toBe(
      "./dev/fixture-replay-blocked",
    );
    const production = {
      EXPO_PUBLIC_RELAY_CHANNEL: "production",
      EXPO_PUBLIC_RELAY_ALLOW_DEMO: "1",
      EXPO_PUBLIC_RELAY_DEV_CONSOLE: "true",
    };
    expect(redirectProductionModule("./createBrowserDemoClient.js", production)).toBe(
      "./demo-blocked",
    );
    expect(redirectProductionModule("./dev/replay-acronym-fixture.js", production)).toBe(
      "./dev/fixture-replay-blocked",
    );
    const internal = {
      EXPO_PUBLIC_RELAY_CHANNEL: "internal",
      EXPO_PUBLIC_RELAY_ALLOW_DEMO: "1",
      EXPO_PUBLIC_RELAY_DEV_CONSOLE: "true",
    };
    expect(redirectProductionModule("./createBrowserDemoClient.js", internal)).toBeNull();
    expect(redirectProductionModule("./dev/replay-acronym-fixture.js", internal)).toBeNull();
  });
});

describe("production static import graph", () => {
  it("keeps testkit, fixtures, and the demo client off production entries", () => {
    const app = readFileSync(resolve(root, "apps/relay/src/App.tsx"), "utf8");
    expect(app).not.toContain("@relay/testkit");
    expect(app).toContain("developerConsoleAllowed");
    expect(app).toContain('import("./dev/replay-acronym-fixture.js")');
    const gate = readFileSync(resolve(root, "apps/relay/src/bootstrap/dev-console.ts"), "utf8");
    expect(gate).toContain("EXPO_PUBLIC_RELAY_CHANNEL");
    expect(gate).toContain("EXPO_PUBLIC_RELAY_DEV_CONSOLE");

    const client = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createAppClient.ts"),
      "utf8",
    );
    expect(client).not.toMatch(/from\s+["']\.\/createBrowserDemoClient/);
    expect(client).toContain('import("./createBrowserDemoClient.js")');

    const { files, unresolved } = productionGraph();
    expect(unresolved).toEqual([]);
    const violations: string[] = [];
    for (const file of files) {
      const rel = relative(root, file);
      if (rel.startsWith("apps/relay/src/dev/"))
        violations.push(`${rel} is in the dev fixture tree`);
      if (rel.endsWith("createBrowserDemoClient.ts")) violations.push(`${rel} is the demo client`);
      const source = readFileSync(file, "utf8");
      for (const spec of staticSpecifiers(source)) {
        if (
          spec === "@relay/testkit" ||
          spec.startsWith("@relay/testkit/") ||
          spec.includes("recorded-judgments") ||
          spec.includes("/fixtures/")
        ) {
          violations.push(`${rel} statically imports ${spec}`);
        }
      }
    }
    expect(violations).toEqual([]);
    expect(files.some((file) => file.endsWith("createWebClient.ts"))).toBe(false);
    expect(files.some((file) => file.endsWith("createBrowserDemoClient.ts"))).toBe(false);
    expect(files.some((file) => file.includes(`${join("apps", "relay", "src", "dev")}`))).toBe(
      false,
    );
    expect(files.length).toBeGreaterThan(10);
  });
});

const BUNDLE_FORBIDDEN = [
  "@relay/testkit",
  "ACRONYM_BASIC_JSONL",
  "ACRONYM_BASIC_EVENTS",
  "MemoryEngineStore",
  "MemoryArtifactStore",
  "We should check the API before launch.",
  "recorded-judgments",
];

async function bundleText(dir: string): Promise<string> {
  const files = await collectFiles(dir);
  const parts: string[] = [];
  for (const file of files) {
    if (file.endsWith(".hbc")) {
      parts.push((await readFile(file)).toString("latin1"));
    } else if (file.endsWith(".js") || file.endsWith(".html") || file.endsWith(".json")) {
      parts.push(await readFile(file, "utf8"));
    }
  }
  return parts.join("\n");
}

async function collectFiles(dir: string): Promise<string[]> {
  const out: string[] = [];
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...(await collectFiles(full)));
    else out.push(full);
  }
  return out;
}

describe("production export purity", () => {
  it("keeps fixtures out of web, iOS, and Android exports when dev flags are off", async () => {
    const env = { ...process.env };
    delete env.EXPO_PUBLIC_RELAY_ALLOW_DEMO;
    delete env.EXPO_PUBLIC_RELAY_DEV_CONSOLE;
    env.EXPO_PUBLIC_RELAY_CHANNEL = "production";
    for (const platform of ["web", "ios", "android"] as const) {
      const dir = await mkdtemp(join(tmpdir(), `relay-${platform}-`));
      try {
        const expoCli = resolve(root, "node_modules/expo/bin/cli");
        const command = existsSync(expoCli)
          ? process.execPath
          : process.platform === "win32"
            ? "npx.cmd"
            : "npx";
        const args = existsSync(expoCli)
          ? [expoCli, "export", "--platform", platform, "--output-dir", dir]
          : ["expo", "export", "--platform", platform, "--output-dir", dir];
        await execFileAsync(command, args, {
          cwd: resolve(root, "apps/relay"),
          env,
          timeout: 180_000,
          maxBuffer: 8 * 1024 * 1024,
          shell: !existsSync(expoCli) && process.platform === "win32",
        });
        const text = await bundleText(dir);
        const hits = BUNDLE_FORBIDDEN.filter((needle) => text.includes(needle));
        expect(hits, platform).toEqual([]);
      } finally {
        await rm(dir, { recursive: true, force: true });
      }
    }
  }, 180_000);
});
