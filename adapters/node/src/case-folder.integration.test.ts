import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { Pass1Foundation, SealedCaseFolder, foundationFromEngineStore } from "@relay/engine";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { openSqliteEngineStore } from "./sqlite-store.js";

describe("durable Case folder", () => {
  it("reads the Case Intent back from sqlite and artifacts after reopen", async () => {
    const dir = await mkdtemp(join(tmpdir(), "relay-case-folder-"));
    const path = join(dir, "state.sqlite");
    try {
      const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(path));
      const store = await openSqliteEngineStore(path, artifacts);
      const records = foundationFromEngineStore(store);
      expect(records).not.toBeNull();
      const folder = new SealedCaseFolder(artifacts, records!);
      const first = new Pass1Foundation({
        records: records!,
        folder,
        artifacts,
        clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
        ids: { next: (prefix) => `${prefix}_disk` },
        inspectConnection: async () => ({
          healthStatus: "authority_recorded",
          observationEnabled: true,
          selectedResources: ["calendar:birthdays"],
        }),
      });
      await first.ensureSeeded();
      store.close();

      const artifacts2 = new FileArtifactStore(fileArtifactRootForDatabase(path));
      const reopened = await openSqliteEngineStore(path, artifacts2);
      const records2 = foundationFromEngineStore(reopened)!;
      const folder2 = new SealedCaseFolder(artifacts2, records2);
      const second = new Pass1Foundation({
        records: records2,
        folder: folder2,
        artifacts: artifacts2,
        clock: { now: () => new Date("2026-09-24T12:05:00.000Z") },
        ids: { next: (prefix) => `${prefix}_re` },
      });
      await second.ensureSeeded();
      const markdown = new TextDecoder().decode((await folder2.read("case_birthdays", "main.md")) ?? new Uint8Array());
      expect(markdown.startsWith("## Case Intent")).toBe(true);
      expect(markdown).toContain("Remember who has a birthday");
      const index = await records2.get("project_case", "case_birthdays");
      expect(JSON.stringify(index?.payload)).not.toContain("Remember who");
      reopened.close();
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
