import { DatabaseSync } from "node:sqlite";
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { SqliteEngineStore } from "./sqlite-store.js";

const LEGACY_MEMORY = "LEGACY_MEMORY_SENTINEL_008_a1b2";
const LEGACY_LABEL = "LEGACY_LABEL_SENTINEL_008_c3d4";
const LEGACY_BECAUSE = "LEGACY_BECAUSE_SENTINEL_008_e5f6";
const LEGACY_FINDING = "LEGACY_FINDING_SENTINEL_008_g7h8";
const ALL = [LEGACY_MEMORY, LEGACY_LABEL, LEGACY_BECAUSE, LEGACY_FINDING] as const;

const migrationDir = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../../../packages/storage-schema/migrations",
);

describe("legacy protected content migration 008", () => {
  it("scrubs pre-protection plaintext on open, hydrates, and stays idempotent", async () => {
    const root = mkdtempSync(join(tmpdir(), "relay-legacy-008-"));
    const dbPath = join(root, "state.sqlite");
    try {
      seedPreProtectionDb(dbPath);

      const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(dbPath));
      const store = await SqliteEngineStore.open(dbPath, artifacts);

      const memories = await store.learning.listMemories();
      expect(memories.some((m) => m.value.expansion === LEGACY_MEMORY)).toBe(true);

      const receipts = await store.learning.listReceipts();
      const receipt = receipts.find((r) => Object.values(r.optionLabels).includes(LEGACY_LABEL));
      expect(receipt).toBeTruthy();
      expect(receipt?.selectedOptionId).toBe("opt_a");

      const candidates = await store.learning.listCandidates();
      expect(candidates.some((c) => c.because === LEGACY_BECAUSE)).toBe(true);

      const reviews = await store.learning.listReviews();
      expect(reviews.some((r) => r.findings.includes(LEGACY_FINDING))).toBe(true);

      const sqliteBytes = readFileSync(dbPath);
      for (const sentinel of ALL) {
        expect(sqliteBytes.includes(Buffer.from(sentinel))).toBe(false);
      }

      const migrated = new DatabaseSync(dbPath);
      const row = migrated
        .prepare("SELECT version FROM schema_migrations WHERE version = 8")
        .get() as { version: number } | undefined;
      expect(row?.version).toBe(8);
      migrated.close();

      store.close();

      const again = await SqliteEngineStore.open(
        dbPath,
        new FileArtifactStore(fileArtifactRootForDatabase(dbPath)),
      );
      const memories2 = await again.learning.listMemories();
      expect(memories2.some((m) => m.value.expansion === LEGACY_MEMORY)).toBe(true);
      const sqliteBytes2 = readFileSync(dbPath);
      for (const sentinel of ALL) {
        expect(sqliteBytes2.includes(Buffer.from(sentinel))).toBe(false);
      }
      again.close();
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});

function seedPreProtectionDb(dbPath: string): void {
  mkdirSync(dirname(dbPath), { recursive: true });
  const db = new DatabaseSync(dbPath);
  db.exec("PRAGMA foreign_keys = ON");
  const now = new Date().toISOString();
  const files: Record<number, string> = {
    1: "001_core.sql",
    2: "002_learning.sql",
    3: "003_runtime.sql",
    4: "004_decisions.sql",
    5: "005_content_artifacts.sql",
    6: "006_protected_learning.sql",
    7: "007_runtime_settings.sql",
  };
  db.exec("BEGIN");
  db.exec(readFileSync(join(migrationDir, files[1]!), "utf8"));
  db.prepare("INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, ?)").run(now);
  for (const version of [2, 3, 4, 5, 6, 7]) {
    db.exec(readFileSync(join(migrationDir, files[version]!), "utf8"));
    db.prepare("INSERT INTO schema_migrations(version, applied_at) VALUES (?, ?)").run(version, now);
  }
  db.prepare(
    `INSERT INTO memories(memory_id, kind, key, value_json, source, created_at, metadata_json)
     VALUES (?, 'glossary', 'ABC', ?, 'explicit_user', ?, '{}')`,
  ).run("mem_legacy", JSON.stringify({ expansion: LEGACY_MEMORY, status: "confirmed" }), now);
  db.prepare(
    `INSERT INTO decision_receipts(
       receipt_id, case_id, gate_id, policy_version, question_type, provider,
       probabilities_json, thresholds_json, selected_option, result, reason_code,
       latency_ms, retries, created_at, option_labels_json, selected_option_id
     ) VALUES (?, NULL, 'gate', 'v1', 'choice', 'recorded', ?, '{}', ?, 'pass', 'ok',
       1, 0, ?, ?, NULL)`,
  ).run(
    "rcpt_legacy",
    JSON.stringify({ opt_a: 0.9, no_match: 0.1 }),
    LEGACY_LABEL,
    now,
    JSON.stringify({ opt_a: LEGACY_LABEL, no_match: "none" }),
  );
  db.prepare(
    `INSERT INTO expansion_candidates(candidate_id, signature, state, because, needed, updated_at)
     VALUES (?, 'sig', 'candidate', ?, 'evidence', ?)`,
  ).run("cand_legacy", LEGACY_BECAUSE, now);
  db.prepare(
    `INSERT INTO review_runs(
       review_id, trigger_code, at, findings_json,
       sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review
     ) VALUES (?, 'sessions', ?, ?, 12, 0, 0, 0)`,
  ).run("rev_legacy", now, JSON.stringify([LEGACY_FINDING]));
  db.exec("COMMIT");
  db.close();
}
