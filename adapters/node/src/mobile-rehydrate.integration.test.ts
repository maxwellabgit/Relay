import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { localOnlyPolicy } from "@relay/contracts";
import type { ByteFilePort, SecretStore } from "@relay/adapter-expo";
import { openMobileBackend } from "@relay/adapter-expo";
import { wrapNodeSqlite } from "./sqlite-store.js";

const SECRET_PROSE = "private-note-prose-should-not-hit-sqlite";

class DirFiles implements ByteFilePort {
  constructor(private readonly root: string) {
    mkdirSync(root, { recursive: true });
  }

  async write(name: string, bytes: Uint8Array): Promise<void> {
    writeFileSync(join(this.root, name), bytes);
  }

  async read(name: string): Promise<Uint8Array | null> {
    try {
      return new Uint8Array(readFileSync(join(this.root, name)));
    } catch {
      return null;
    }
  }
}

class FileSecrets implements SecretStore {
  constructor(private readonly path: string) {}

  private load(): Record<string, string> {
    try {
      return JSON.parse(readFileSync(this.path, "utf8")) as Record<string, string>;
    } catch {
      return {};
    }
  }

  async get(key: string): Promise<string | null> {
    return this.load()[key] ?? null;
  }

  async set(key: string, value: string): Promise<void> {
    const next = this.load();
    next[key] = value;
    writeFileSync(this.path, JSON.stringify(next));
  }

  async delete(key: string): Promise<void> {
    const next = this.load();
    delete next[key];
    writeFileSync(this.path, JSON.stringify(next));
  }
}

describe("mobile durable composition", () => {
  it("reopens sqlite state and decrypts artifacts without storing prose in sqlite", async () => {
    const root = mkdtempSync(join(tmpdir(), "relay-mobile-"));
    const dbPath = join(root, "relay.db");
    const files = new DirFiles(join(root, "objects"));
    const secrets = new FileSecrets(join(root, "secrets.json"));
    try {
      const first = await openMobileBackend({
        database: wrapNodeSqlite(dbPath),
        files,
        secrets,
      });
      const created = await first.store.createCase({
        caseId: "case_mobile_1",
        origin: "direct",
        kind: "answer",
        priority: 1,
        at: "2026-09-22T00:00:00.000Z",
      });
      expect(created.caseId).toBe("case_mobile_1");
      const ref = await first.artifacts.put(new TextEncoder().encode(SECRET_PROSE), localOnlyPolicy());
      first.close();

      const sqliteBytes = readFileSync(dbPath);
      expect(sqliteBytes.includes(Buffer.from(SECRET_PROSE))).toBe(false);

      const second = await openMobileBackend({
        database: wrapNodeSqlite(dbPath),
        files,
        secrets,
      });
      const reloaded = await second.store.getCase("case_mobile_1");
      expect(reloaded?.caseId).toBe("case_mobile_1");
      const plain = new TextDecoder().decode(await second.artifacts.get(ref));
      expect(plain).toBe(SECRET_PROSE);
      const speech = await second.speech.status();
      expect(speech.ok).toBe(false);
      expect(speech.detail).toBe("speech_unavailable");
      await second.lifecycle.background();
      const after = await second.speech.status();
      expect(after.capturing).toBe(false);
      second.close();
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
