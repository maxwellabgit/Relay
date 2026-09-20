import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { calendarBlockEpisode, createResolveAcronymModule } from "@relay/reflexes";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

const TRANSCRIPT_SENTINEL = "TRANSCRIPT_SENTINEL_7f3a9c2e_UNIQUE";
const MODEL_SENTINEL = "MODEL_SENTINEL_7f3a9c2e_UNIQUE";
const MEMORY_SENTINEL = "MEMORY_SENTINEL_7f3a9c2e_UNIQUE";
const JEV_OPTION_SENTINEL = "JEV_OPTION_SENTINEL_7f3a9c2e_UNIQUE";
const REVIEW_SENTINEL = "REVIEW_SENTINEL_7f3a9c2e_UNIQUE";

const ALL = [TRANSCRIPT_SENTINEL, MODEL_SENTINEL, MEMORY_SENTINEL, JEV_OPTION_SENTINEL, REVIEW_SENTINEL] as const;

describe("protected learning privacy boundary", () => {
  it("keeps all sentinels out of sqlite bytes and run logs while hydrating via artifacts", async () => {
    const root = mkdtempSync(join(tmpdir(), "relay-protected-privacy-"));
    const dbPath = join(root, "state.sqlite");
    const runsRoot = join(root, "runs");
    try {
      const harness = createNodeHarness({
        databasePath: dbPath,
        runsRoot,
        sessionId: "session_privacy",
        episodeDefinitions: [calendarBlockEpisode],
        reflexModules: [
          createResolveAcronymModule({
            glossary: {
              exactUser: async () => null,
              exactProject: async () => null,
              exactBundled: async () => null,
              searchWindow: async (token) =>
                token === "XYZ" ? [JEV_OPTION_SENTINEL, "Other Option"] : [],
            },
          }),
        ],
        judgments: {
          async judge(request) {
            if (request.questionSetId === "judgment.acronym-choice") {
              return recordedSuccess({
                expansion: {
                  type: "choice",
                  choice: JEV_OPTION_SENTINEL,
                  probabilities: { [JEV_OPTION_SENTINEL]: 0.9, "Other Option": 0.05, no_match: 0.05 },
                  confidence: 0.9,
                },
                useful: { type: "noul", probabilityYes: 0.9 },
              });
            }
            return recordedSuccess({
              benefit: { type: "noul", probabilityYes: 0.8 },
            });
          },
        },
        model: {
          async generate() {
            return { ok: true, text: MODEL_SENTINEL, model: "test", elapsedMs: 1 };
          },
        },
      });

      await harness.client.start();
      await harness.client.execute({ type: "SetListening", enabled: true });
      await harness.engine.ingestFinalSegment(
        {
          schemaVersion: 1,
          sourceId: "mic",
          sessionId: "session_privacy",
          segmentId: "seg_privacy_1",
          revision: 1,
          sequence: 1,
          origin: "microphone",
          speakerKey: null,
          speakerConfidence: null,
          startMs: 0,
          endMs: 1000,
          text: TRANSCRIPT_SENTINEL,
          textConfidence: null,
          final: true,
          cursor: null,
        },
        false,
      );
      await wait(80);

      await harness.client.execute({
        type: "UpsertGlossaryEntry",
        token: "ABC",
        expansion: MEMORY_SENTINEL,
        confirmed: true,
      });

      await harness.client.execute({ type: "SubmitText", text: "What does XYZ mean?" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );

      await harness.client.execute({ type: "SubmitText", text: "What is the capital of France?" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.some((item) => item.summary === MODEL_SENTINEL),
      );

      await harness.store.learning.putReview({
        reviewId: "review_privacy_1",
        triggerCode: "sessions",
        at: new Date().toISOString(),
        findings: [REVIEW_SENTINEL],
        sessionsAtReview: 12,
        episodesAtReview: 0,
        candidatesAtReview: 0,
        builtReflexesAtReview: 0,
      });

      const memories = await harness.store.learning.listMemories();
      expect(memories.some((m) => m.value.expansion === MEMORY_SENTINEL)).toBe(true);

      const receipts = await harness.store.learning.listReceipts();
      expect(receipts.some((r) => Object.values(r.optionLabels).includes(JEV_OPTION_SENTINEL))).toBe(true);

      const reviews = await harness.store.learning.listReviews();
      expect(reviews.some((r) => r.findings.includes(REVIEW_SENTINEL))).toBe(true);

      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.some((item) => item.summary === MODEL_SENTINEL)).toBe(true);

      const sqliteBytes = readFileSync(dbPath);
      for (const sentinel of ALL) {
        expect(sqliteBytes.includes(Buffer.from(sentinel))).toBe(false);
      }

      const runDirs = readdirSync(runsRoot).filter((d) => d.startsWith("run_"));
      expect(runDirs.length).toBeGreaterThan(0);
      for (const runDir of runDirs) {
        const eventsPath = join(runsRoot, runDir, "events.jsonl");
        if (existsSync(eventsPath)) {
          const eventsText = readFileSync(eventsPath, "utf8");
          for (const sentinel of ALL) {
            expect(eventsText).not.toContain(sentinel);
          }
        }
        const manifestPath = join(runsRoot, runDir, "manifest.json");
        if (existsSync(manifestPath)) {
          const manifestText = readFileSync(manifestPath, "utf8");
          for (const sentinel of ALL) {
            expect(manifestText).not.toContain(sentinel);
          }
        }
      }

      await harness.client.stop();
      harness.close();
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitFor(predicate: () => Promise<boolean>): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < 4000) {
    if (await predicate()) return;
    await wait(25);
  }
  throw new Error("timeout");
}
