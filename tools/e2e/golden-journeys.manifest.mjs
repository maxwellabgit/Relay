/**
 * Canonical golden journey IDs 01–12.
 * proofKind values:
 *   - headed-product: headed installed/desktop product harness
 *   - integration-lower-layer: Vitest integration (not V1 product proof)
 *   - missing-harness: ID reserved; no runnable harness yet (F6 must fill)
 */
export const GOLDEN_JOURNEY_IDS = Object.freeze([
  "01",
  "02",
  "03",
  "04",
  "05",
  "06",
  "07",
  "08",
  "09",
  "10",
  "11",
  "12",
]);

/** @typedef {"headed-product" | "integration-lower-layer" | "missing-harness"} ProofKind */

/**
 * @type {readonly {
 *   id: string;
 *   number: string;
 *   title: string;
 *   proofKind: ProofKind;
 *   harness: string | null;
 *   vitestFile: string | null;
 * }[]}
 */
export const goldenJourneys = Object.freeze([
  {
    id: "01-deterministic-glossary",
    number: "01",
    title: "Deterministic glossary (MSRP)",
    proofKind: "headed-product",
    harness: "tools/e2e/msrp-desktop-headed.ts",
    vitestFile: null,
  },
  {
    id: "02-helpful-chat",
    number: "02",
    title: "Helpful chat / Ask path",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/tool-kernel.integration.test.ts",
    vitestFile: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "03-tool-assisted-ask",
    number: "03",
    title: "Tool-assisted Ask with public-search",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/tool-kernel.integration.test.ts",
    vitestFile: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "04-ambient-ignore",
    number: "04",
    title: "Ambient ignore noise",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/ambient-triage.integration.test.ts",
    vitestFile: "adapters/node/src/ambient-triage.integration.test.ts",
  },
  {
    id: "05-ambient-note",
    number: "05",
    title: "Ambient note recommendation",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/ambient-triage.integration.test.ts",
    vitestFile: "adapters/node/src/ambient-triage.integration.test.ts",
  },
  {
    id: "06-foreground-listening",
    number: "06",
    title: "Foreground listening session",
    proofKind: "missing-harness",
    harness: null,
    vitestFile: null,
  },
  {
    id: "07-ambiguous-acronym",
    number: "07",
    title: "Ambiguous acronym + bounded Jev",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/bounded-expansion.integration.test.ts",
    vitestFile: "adapters/node/src/bounded-expansion.integration.test.ts",
  },
  {
    id: "08-claim-verification",
    number: "08",
    title: "Claim verification",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/claim-verify.integration.test.ts",
    vitestFile: "adapters/node/src/claim-verify.integration.test.ts",
  },
  {
    id: "09-crash-recovery",
    number: "09",
    title: "Crash recovery / restart",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/windows-v1-crash-recovery.integration.test.ts",
    vitestFile: "adapters/node/src/windows-v1-crash-recovery.integration.test.ts",
  },
  {
    id: "10-write-authority",
    number: "10",
    title: "Write authority / operations",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/tool-kernel.integration.test.ts",
    vitestFile: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "11-pattern-proposal",
    number: "11",
    title: "Pattern proposal",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/bounded-expansion.integration.test.ts",
    vitestFile: "adapters/node/src/bounded-expansion.integration.test.ts",
  },
  {
    id: "12-shadow-activation",
    number: "12",
    title: "Shadow activation lifecycle",
    proofKind: "integration-lower-layer",
    harness: "adapters/node/src/bounded-expansion.integration.test.ts",
    vitestFile: "adapters/node/src/bounded-expansion.integration.test.ts",
  },
]);

export function assertGoldenJourneyManifestIntegrity(journeys = goldenJourneys) {
  const numbers = journeys.map((j) => j.number);
  const expected = [...GOLDEN_JOURNEY_IDS];
  const missing = expected.filter((n) => !numbers.includes(n));
  const unexpected = numbers.filter((n) => !expected.includes(n));
  const seen = new Set();
  const duplicates = [];
  for (const n of numbers) {
    if (seen.has(n)) duplicates.push(n);
    seen.add(n);
  }
  const errors = [];
  if (missing.length) errors.push(`missing journey numbers: ${missing.join(", ")}`);
  if (unexpected.length) errors.push(`unexpected journey numbers: ${unexpected.join(", ")}`);
  if (duplicates.length) errors.push(`duplicate journey numbers: ${duplicates.join(", ")}`);
  if (journeys.length !== 12) errors.push(`expected 12 journeys, found ${journeys.length}`);
  return { ok: errors.length === 0, errors };
}

/** Vitest files for lower-layer golden run (excludes headed + missing). */
export function integrationVitestFiles(journeys = goldenJourneys) {
  return [
    ...new Set(
      journeys
        .filter((j) => j.proofKind === "integration-lower-layer" && j.vitestFile)
        .map((j) => j.vitestFile),
    ),
  ];
}
