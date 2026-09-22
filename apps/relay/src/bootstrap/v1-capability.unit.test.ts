import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  assertCapabilityMatrixIntegrity,
  capabilityStatus,
  hiddenCapabilities,
  V1_APPLICATION_IDS,
  V1_CAPABILITY_MATRIX,
  visibleCapabilities,
} from "@relay/contracts";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");

describe("V1 capability matrix", () => {
  it("has valid statuses and never uses ambiguous wired notes", () => {
    const integrity = assertCapabilityMatrixIntegrity();
    expect(integrity.errors).toEqual([]);
    expect(integrity.ok).toBe(true);
    expect(V1_CAPABILITY_MATRIX.length).toBeGreaterThan(10);
  });

  it("freezes permanent application IDs and marketing version 1.0.0", () => {
    expect(V1_APPLICATION_IDS.mobile).toBe("app.relay.assistant");
    expect(V1_APPLICATION_IDS.desktop).toBe("app.relay.desktop");
    expect(V1_APPLICATION_IDS.marketingVersion).toBe("1.0.0");
  });

  it("hides search and github until a production adapter exists", () => {
    expect(capabilityStatus("tool.public-search", "windows")).toBe("not-shipped");
    expect(capabilityStatus("tool.github-read", "windows")).toBe("not-shipped");
    expect(hiddenCapabilities("windows")).toContain("tool.public-search");
    expect(hiddenCapabilities("windows")).toContain("tool.github-read");
    expect(visibleCapabilities("windows")).not.toContain("tool.public-search");
  });

  it("records mobile storage as present but unverified, and stubs as not shipped", () => {
    expect(capabilityStatus("storage.sqlite", "ios")).toBe("unverified-on-device");
    expect(capabilityStatus("secrets.platform", "android")).toBe("unverified-on-device");
    expect(capabilityStatus("model.mobile-tiny", "ios")).toBe("not-shipped");
    expect(capabilityStatus("ask.typed", "android")).toBe("not-shipped");
    expect(capabilityStatus("jev.hosted", "ios")).toBe("degraded");
  });
});

describe("production composition purity", () => {
  const forbidden = [
    "MemoryEngineStore",
    "MemoryArtifactStore",
    "RecordedJudgmentPort",
    "@relay/testkit",
  ];

  it("createDesktopClient does not import testkit or memory demo stores", () => {
    const text = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts"),
      "utf8",
    );
    for (const token of forbidden) {
      expect(text).not.toContain(token);
    }
    expect(text).toContain("TauriArtifactStore");
    expect(text).toContain("TauriEngineStore");
  });

  it("createAppClient does not import testkit; demo only behind explicit flag", () => {
    const text = readFileSync(resolve(root, "apps/relay/src/bootstrap/createAppClient.ts"), "utf8");
    expect(text).not.toContain("@relay/testkit");
    expect(text).not.toContain("MemoryEngineStore");
    expect(text).toContain("EXPO_PUBLIC_RELAY_ALLOW_DEMO");
    expect(text).toContain("createMobileClient");
    expect(text).toContain("isNativeMobile");
    expect(text).toContain("isExplicitDemoAllowed");
  });

  it("production desktop and mobile clients do not inject public search or github", () => {
    const desktop = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts"),
      "utf8",
    );
    const mobile = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createMobileClient.native.ts"),
      "utf8",
    );
    for (const text of [desktop, mobile]) {
      expect(text).not.toMatch(/publicSearch\s*:/);
      expect(text).not.toMatch(/github\s*:/);
    }
  });

  it("createMobileClient uses the expo durable backend and not testkit", () => {
    const text = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createMobileClient.native.ts"),
      "utf8",
    );
    expect(text).toContain("openMobileBackend");
    expect(text).toContain("createExpoSecureSecretStore");
    expect(text).toContain("createExpoDocumentFiles");
    expect(text).not.toContain("new MemorySecretStore");
    expect(text).not.toContain("new MemoryByteFiles");
    expect(text).not.toContain("@relay/testkit");
    expect(text).not.toContain("MemoryEngineStore");
    expect(text).not.toContain("MemoryArtifactStore");
    expect(text).toContain("createTypeSafeJudgmentPort");
    expect(text).toContain("createMobileTraceSink");
    expect(text).toContain("applySpeechSuspend");
    expect(text).toContain("speechOnRelaunch");
    expect(text).not.toContain("createBrowserTraceSink");
    expect(text).not.toContain("/__relay/trace");
  });

  it("Expo app config freezes permanent mobile id and marketing 1.0.0", () => {
    const text = readFileSync(resolve(root, "apps/relay/app.config.ts"), "utf8");
    expect(text).toContain(`bundleIdentifier: "${V1_APPLICATION_IDS.mobile}"`);
    expect(text).toContain(`package: "${V1_APPLICATION_IDS.mobile}"`);
    expect(text).toContain(`version: "${V1_APPLICATION_IDS.marketingVersion}"`);
    expect(text).not.toMatch(/bundleIdentifier:\s*["']com\.neptranslate/i);
    expect(text).not.toMatch(/package:\s*["']com\.neptranslate/i);
  });

  it("desktop Tauri config freezes permanent desktop id and marketing 1.0.0", () => {
    const conf = JSON.parse(
      readFileSync(resolve(root, "apps/desktop/src-tauri/tauri.conf.json"), "utf8"),
    ) as { identifier: string; version: string };
    expect(conf.identifier).toBe(V1_APPLICATION_IDS.desktop);
    expect(conf.version).toBe(V1_APPLICATION_IDS.marketingVersion);
  });

  it("desktop package depends on matching @relay/app marketing version (workspace)", () => {
    const desktop = JSON.parse(
      readFileSync(resolve(root, "apps/desktop/package.json"), "utf8"),
    ) as { version: string; dependencies: Record<string, string> };
    const app = JSON.parse(readFileSync(resolve(root, "apps/relay/package.json"), "utf8")) as {
      version: string;
    };
    expect(app.version).toBe(V1_APPLICATION_IDS.marketingVersion);
    expect(desktop.version).toBe(V1_APPLICATION_IDS.marketingVersion);
    expect(desktop.dependencies["@relay/app"]).toBe(app.version);
  });

  it("does not keep the unused pending mobile judgment transport", () => {
    const roots = [
      resolve(root, "adapters/expo/src"),
      resolve(root, "apps/relay/src"),
    ];
    const files: string[] = [];
    const visit = (dir: string) => {
      if (!existsSync(dir)) return;
      for (const entry of readdirSync(dir)) {
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
          if (entry === "node_modules" || entry === "dist") continue;
          visit(full);
          continue;
        }
        if (entry.endsWith(".test.ts") || entry.endsWith(".test.tsx")) continue;
        if (entry.endsWith(".ts") || entry.endsWith(".tsx")) files.push(full);
      }
    };
    for (const dir of roots) visit(dir);
    const hits = files.filter((file) =>
      readFileSync(file, "utf8").includes("native_system_one_pending_dev_client"),
    );
    expect(hits).toEqual([]);
    const mobile = readFileSync(
      resolve(root, "apps/relay/src/bootstrap/createMobileClient.native.ts"),
      "utf8",
    );
    expect(mobile).toContain("createTypeSafeJudgmentPort");
  });

  it("product contract document exists and references the capability module", () => {
    const text = readFileSync(resolve(root, "docs/V1_PRODUCT_CONTRACT.md"), "utf8");
    expect(text).toContain("V1_CAPABILITY_MATRIX");
    expect(text).toContain("app.relay.assistant");
    expect(text).toContain("1.0.0");
    expect(text).toContain("EXPO_PUBLIC_RELAY_ALLOW_DEMO");
    expect(text).not.toMatch(/\bis wired\b/i);
  });
});
