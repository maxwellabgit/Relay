import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

type NpmRunRef = {
  script: string;
  workspace: string | null;
};

function packageScripts(packageJsonPath: string): Record<string, string> {
  const pkg = JSON.parse(readFileSync(packageJsonPath, "utf8")) as {
    scripts?: Record<string, string>;
  };
  return pkg.scripts ?? {};
}

function npmRunRefsInManifest(): NpmRunRef[] {
  const text = readFileSync(resolve(root, "tools/verification/manifest.mjs"), "utf8");
  const refs: NpmRunRef[] = [];
  const stepBlocks = [...text.matchAll(/\{[^{}]*command:\s*"npm"[^{}]*\}/gs)];
  for (const block of stepBlocks) {
    const body = block[0]!;
    const script = body.match(/args:\s*\[[^\]]*"run"\s*,\s*"([^"]+)"/)?.[1];
    if (!script) continue;
    const workspace = body.match(/"--workspace"\s*,\s*"([^"]+)"/)?.[1] ?? null;
    refs.push({ script, workspace });
  }
  return refs;
}

function workspacePackageJson(workspaceName: string): string {
  const map: Record<string, string> = {
    "@relay/app": "apps/relay/package.json",
    "@relay/desktop": "apps/desktop/package.json",
  };
  const relative = map[workspaceName];
  if (!relative) {
    throw new Error(`unmapped workspace ${workspaceName}`);
  }
  return resolve(root, relative);
}

describe("verification script integrity", () => {
  it("manifest npm scripts exist in the targeted package.json", () => {
    const rootScripts = packageScripts(resolve(root, "package.json"));
    const missing: string[] = [];
    for (const ref of npmRunRefsInManifest()) {
      const scripts = ref.workspace
        ? packageScripts(workspacePackageJson(ref.workspace))
        : rootScripts;
      if (!(ref.script in scripts)) {
        missing.push(ref.workspace ? `${ref.workspace}:${ref.script}` : ref.script);
      }
    }
    expect(missing).toEqual([]);
  });

  it("GitHub Actions check workflow delegates to verify:v1 (no stale npm run list)", () => {
    const workflow = readFileSync(resolve(root, ".github/workflows/check.yml"), "utf8");
    expect(workflow).toContain("npm run verify:v1");
    expect(workflow).toMatch(/timeout-minutes:\s*45/);
    expect(workflow).not.toMatch(/npm run test:smoke/);
    expect(workflow).not.toMatch(/npm run build:desktop/);
  });

  it("verify:v1 runner imports the shared manifest and streams child output", () => {
    const runner = readFileSync(resolve(root, "tools/verify-v1.mjs"), "utf8");
    expect(runner).toContain("verification/manifest.mjs");
    expect(runner).toContain('stdio: ["ignore", "inherit", "inherit"]');
    expect(runner).toContain("latest-summary.json");
    const manifest = readFileSync(resolve(root, "tools/verification/manifest.mjs"), "utf8");
    expect(manifest).toContain('name: "smoke"');
    expect(manifest).toContain('name: "halo-install"');
    expect(manifest).toContain('name: "desktop-build"');
  });

  it("package.json keeps smoke and manual MSRP as distinct commands", () => {
    const scripts = packageScripts(resolve(root, "package.json"));
    expect(scripts["test:smoke"]).toContain("production-smoke.integration.test.ts");
    expect(scripts["test:manual:msrp"]).toContain("msrp-preflight.ts");
    expect(scripts["test:e2e:msrp"]).toContain("msrp-desktop-headed.ts");
  });
});
