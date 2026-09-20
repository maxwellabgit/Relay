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

  it("enables Listen only after audio start succeeds", () => {
    const path = resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts");
    const text = readFileSync(path, "utf8");
    const enableBlock = text.slice(text.indexOf('if (command.enabled)'));
    const audioStartAt = enableBlock.indexOf("audio.start");
    const executeAt = enableBlock.indexOf("inner.execute(command)");
    expect(audioStartAt).toBeGreaterThan(-1);
    expect(executeAt).toBeGreaterThan(audioStartAt);
    expect(text).toMatch(/summary:\s*"audio_unavailable"/);
  });

  it("disables Listen only after stop, drain, and commit", () => {
    const path = resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts");
    const text = readFileSync(path, "utf8");
    const disableIdx = text.indexOf("stop accepting → stop source → drain → commit");
    expect(disableIdx).toBeGreaterThan(-1);
    const stopAt = text.indexOf("audio.stop()", disableIdx);
    const drainAt = text.indexOf("audio.drain()", stopAt);
    const ingestAt = text.indexOf("ingestFinalSegment", drainAt);
    const listenOffAt = text.indexOf("inner.execute(command)", ingestAt);
    expect(stopAt).toBeGreaterThan(-1);
    expect(drainAt).toBeGreaterThan(stopAt);
    expect(ingestAt).toBeGreaterThan(drainAt);
    expect(listenOffAt).toBeGreaterThan(ingestAt);
  });

  it("refreshes provider health after config changes and exposes hosted processing gate", () => {
    const path = resolve(root, "apps/relay/src/bootstrap/createDesktopClient.ts");
    const text = readFileSync(path, "utf8");
    expect(text).toMatch(/RefreshProviderHealth/);
    expect(text).toMatch(/SetHostedProcessing/);
    expect(text).toMatch(/HEALTH_POLL_MS/);
    expect(text).toMatch(/hosted off/);
    expect(text).toMatch(/external:ready|external:unavailable/);
  });
});
