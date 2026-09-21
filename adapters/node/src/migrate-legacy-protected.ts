import type { DatabaseSync } from "node:sqlite";
import type { ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { packMemoryValue, putJsonArtifact, type MemoryKind } from "@relay/engine";

/**
 * Idempotent scrub of pre-006 plaintext into protected artifacts.
 * Safe to run on every open after schema migrations.
 */
export async function migrateLegacyProtectedContent(
  db: DatabaseSync,
  artifacts: ArtifactStorePort,
): Promise<{ memories: number; receipts: number; candidates: number; reviews: number }> {
  const counts = { memories: 0, receipts: 0, candidates: 0, reviews: 0 };
  counts.memories = await migrateMemories(db, artifacts);
  counts.receipts = await migrateReceipts(db, artifacts);
  counts.candidates = await migrateCandidates(db, artifacts);
  counts.reviews = await migrateReviews(db, artifacts);
  return counts;
}

async function migrateMemories(db: DatabaseSync, artifacts: ArtifactStorePort): Promise<number> {
  const rows = db
    .prepare(
      `SELECT memory_id, kind, key, value_json, content_artifact_id, metadata_json
       FROM memories
       WHERE value_json IS NOT NULL AND value_json != '{}'
         AND (content_artifact_id IS NULL OR content_artifact_id = '')`,
    )
    .all() as Array<{
    memory_id: string;
    kind: MemoryKind;
    key: string;
    value_json: string;
    content_artifact_id: string | null;
    metadata_json: string | null;
  }>;
  let n = 0;
  for (const row of rows) {
    let value: Record<string, string>;
    try {
      value = JSON.parse(row.value_json) as Record<string, string>;
    } catch {
      continue;
    }
    if (!value || Object.keys(value).length === 0) {
      db.prepare(`UPDATE memories SET value_json = '{}' WHERE memory_id = ?`).run(row.memory_id);
      n += 1;
      continue;
    }
    const packed = packMemoryValue(row.kind, value);
    const existingMeta = safeObject(row.metadata_json);
    const metadata = { ...existingMeta, ...packed.metadata };
    const ref = await artifacts.put(
      new TextEncoder().encode(JSON.stringify(packed.prose)),
      localOnlyPolicy(),
    );
    db.prepare(
      `UPDATE memories
       SET content_artifact_id = ?, content_sha256 = ?, metadata_json = ?, value_json = '{}'
       WHERE memory_id = ?`,
    ).run(ref.artifactId, ref.sha256, JSON.stringify(metadata), row.memory_id);
    n += 1;
  }
  return n;
}

async function migrateReceipts(db: DatabaseSync, artifacts: ArtifactStorePort): Promise<number> {
  const rows = db
    .prepare(
      `SELECT receipt_id, selected_option, selected_option_id, option_labels_json,
              labels_artifact_id, labels_sha256
       FROM decision_receipts
       WHERE (option_labels_json IS NOT NULL AND option_labels_json != '{}')
          OR (selected_option IS NOT NULL AND selected_option != ''
              AND (selected_option_id IS NULL OR selected_option_id = ''))`,
    )
    .all() as Array<{
    receipt_id: string;
    selected_option: string | null;
    selected_option_id: string | null;
    option_labels_json: string | null;
    labels_artifact_id: string | null;
    labels_sha256: string | null;
  }>;
  let n = 0;
  for (const row of rows) {
    const labels = safeObject(row.option_labels_json);
    let labelsArtifactId = row.labels_artifact_id;
    let labelsSha256 = row.labels_sha256;
    if (Object.keys(labels).length > 0 && (!labelsArtifactId || !labelsSha256)) {
      const ref = await putJsonArtifact(artifacts, labels);
      labelsArtifactId = ref.artifactId;
      labelsSha256 = ref.sha256;
    }
    const selectedId = resolveSelectedOptionId(row.selected_option, row.selected_option_id, labels);
    db.prepare(
      `UPDATE decision_receipts
       SET labels_artifact_id = ?, labels_sha256 = ?, option_labels_json = '{}',
           selected_option = ?, selected_option_id = ?
       WHERE receipt_id = ?`,
    ).run(
      labelsArtifactId,
      labelsSha256,
      selectedId,
      selectedId,
      row.receipt_id,
    );
    n += 1;
  }
  return n;
}

async function migrateCandidates(db: DatabaseSync, artifacts: ArtifactStorePort): Promise<number> {
  const rows = db
    .prepare(
      `SELECT candidate_id, because, because_artifact_id, because_sha256
       FROM expansion_candidates
       WHERE because IS NOT NULL AND because != ''
         AND (because_artifact_id IS NULL OR because_artifact_id = '')`,
    )
    .all() as Array<{
    candidate_id: string;
    because: string;
    because_artifact_id: string | null;
    because_sha256: string | null;
  }>;
  let n = 0;
  for (const row of rows) {
    const ref = await artifacts.put(new TextEncoder().encode(row.because), localOnlyPolicy());
    db.prepare(
      `UPDATE expansion_candidates
       SET because_artifact_id = ?, because_sha256 = ?, because = ''
       WHERE candidate_id = ?`,
    ).run(ref.artifactId, ref.sha256, row.candidate_id);
    n += 1;
  }
  return n;
}

async function migrateReviews(db: DatabaseSync, artifacts: ArtifactStorePort): Promise<number> {
  const rows = db
    .prepare(
      `SELECT review_id, findings_json, findings_artifact_id, findings_sha256
       FROM review_runs
       WHERE findings_json IS NOT NULL AND findings_json != '[]'
         AND (findings_artifact_id IS NULL OR findings_artifact_id = '')`,
    )
    .all() as Array<{
    review_id: string;
    findings_json: string;
    findings_artifact_id: string | null;
    findings_sha256: string | null;
  }>;
  let n = 0;
  for (const row of rows) {
    let findings: string[];
    try {
      findings = JSON.parse(row.findings_json) as string[];
    } catch {
      continue;
    }
    if (!Array.isArray(findings) || findings.length === 0) {
      db.prepare(`UPDATE review_runs SET findings_json = '[]' WHERE review_id = ?`).run(row.review_id);
      n += 1;
      continue;
    }
    const ref = await putJsonArtifact(artifacts, findings);
    db.prepare(
      `UPDATE review_runs
       SET findings_artifact_id = ?, findings_sha256 = ?, findings_json = '[]'
       WHERE review_id = ?`,
    ).run(ref.artifactId, ref.sha256, row.review_id);
    n += 1;
  }
  return n;
}

function resolveSelectedOptionId(
  selectedOption: string | null,
  selectedOptionId: string | null,
  labels: Record<string, string>,
): string | null {
  if (selectedOptionId && selectedOptionId.length > 0) return selectedOptionId;
  if (!selectedOption) return null;
  if (Object.prototype.hasOwnProperty.call(labels, selectedOption)) return selectedOption;
  for (const [id, label] of Object.entries(labels)) {
    if (label === selectedOption) return id;
  }
  return selectedOption;
}

function safeObject(raw: string | null | undefined): Record<string, string> {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as unknown;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};
    const out: Record<string, string> = {};
    for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (typeof value === "string") out[key] = value;
    }
    return out;
  } catch {
    return {};
  }
}
