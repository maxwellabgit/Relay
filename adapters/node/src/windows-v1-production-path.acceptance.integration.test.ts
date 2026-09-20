import { mkdtemp, readFile, readdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createResolveAcronymModule } from "@relay/reflexes";
import { createNodeHarness } from "./create-client.js";

/**
 * Windows V1 production-path acceptance test.
 *
 * Uses the Windows-equivalent Node composition (SQLite + durable artifacts +
 * file trace) to prove the end-to-end durable loop survives stop/restart.
 */
describe("windows v1 production-path acceptance", () => {
  it("submit text → artifact → case → reflex → feed → receipt → trace → restart", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-windows-v1-"));
    const databasePath = join(root, "state.sqlite");
    const runsRoot = join(root, "runs");

    const first = createNodeHarness({ databasePath, runsRoot });
    try {
      await first.client.start();
      await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      await first.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      await waitFor(async () =>
        (await first.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await first.client.getSnapshot();
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer Suggested Retail Price",
      );
      expect(snap.runtime.storageAdapter).toBe("sqlite");
      const events = await first.store.listDomainEvents(50);
      expect(JSON.stringify(events)).not.toMatch(/Manufacturer Suggested Retail Price/);
      expect(snap.cases.every((item) => item.status !== "waiting")).toBe(true);
    } finally {
      await first.client.stop();
      first.close();
    }

    const second = createNodeHarness({ databasePath, runsRoot });
    try {
      await second.client.start();
      await second.client.execute({ type: "SubmitText", text: "What does MSRP mean?" });
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const snap = await second.client.getSnapshot();
      expect(snap.feedItems.find((item) => item.kind === "answer")?.summary).toContain(
        "Manufacturer Suggested Retail Price",
      );
      const runDirs = await readdir(runsRoot);
      expect(runDirs.length).toBeGreaterThan(0);
      const manifest = JSON.parse(await readFile(join(runsRoot, runDirs[0]!, "manifest.json"), "utf8")) as {
        runId: string;
        protocolVersion: string;
      };
      expect(manifest.runId).toMatch(/^run_/);
      expect(manifest.protocolVersion).toBe("2");
      const eventsText = await readFile(join(runsRoot, runDirs[0]!, "events.jsonl"), "utf8");
      expect(eventsText).not.toContain("Manufacturer Suggested Retail Price");
      expect(eventsText).toMatch(/case\.created|source\.accepted|answer\.committed|policy\.evaluated/);
    } finally {
      await second.client.stop();
      second.close();
    }
  });
});

describe("windows restart recovery checkpoints", () => {
  it("does not silently overwrite conflicting glossary memory", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-restart-"));
    const databasePath = join(root, "state.sqlite");
    const first = createNodeHarness({ databasePath });
    try {
      await first.client.start();
      const saved = await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "ABC",
        expansion: "Alpha Beta Corp",
        confirmed: true,
      });
      expect(saved.ok).toBe(true);
      const conflict = await first.client.execute({
        type: "UpsertGlossaryEntry",
        token: "ABC",
        expansion: "Should Conflict",
        confirmed: true,
      });
      expect(conflict.ok).toBe(false);
      expect(conflict.summary).toBe("conflict");
    } finally {
      await first.client.stop();
      first.close();
    }

    const second = createNodeHarness({ databasePath });
    try {
      await second.client.start();
      const memories = await second.store.learning.listMemories();
      expect(memories.filter((m) => m.kind === "glossary" && m.key === "ABC")).toHaveLength(1);
      expect(memories.find((m) => m.key === "ABC")?.value.expansion).toBe("Alpha Beta Corp");
      await second.client.execute({ type: "SubmitText", text: "What does ABC mean?" });
      await waitFor(async () =>
        (await second.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      expect((await second.client.getSnapshot()).feedItems.find((i) => i.kind === "answer")?.summary).toContain(
        "Alpha Beta Corp",
      );
    } finally {
      await second.client.stop();
      second.close();
    }
  });

  it("keeps ambiguous Jev outcomes free of candidate prose in events.jsonl", async () => {
    const root = await mkdtemp(join(tmpdir(), "relay-judgment-resume-"));
    const databasePath = join(root, "state.sqlite");
    const runsRoot = join(root, "runs");
    const harness = createNodeHarness({
      databasePath,
      runsRoot,
      durableDecisionArtifacts: true,
      reflexModules: [
        createResolveAcronymModule({
          glossary: {
            exactUser: async () => null,
            exactProject: async () => null,
            exactBundled: async () => null,
            searchWindow: async (token) =>
              token === "BESS" ? ["Battery Energy Storage", "Bessemer"] : [],
          },
        }),
      ],
      judgments: {
        async judge() {
          return {
            ok: true,
            success: {
              model: "recorded",
              answers: {
                expansion: {
                  type: "choice",
                  choice: "Battery Energy Storage",
                  probabilities: {
                    "Battery Energy Storage": 0.82,
                    Bessemer: 0.1,
                    no_match: 0.08,
                  },
                  confidence: 0.82,
                },
                useful: { type: "noul", probabilityYes: 0.9 },
              },
              inputTokens: 1,
              outputTokens: 1,
              elapsedMs: 1,
            },
          };
        },
      },
    });
    try {
      await harness.client.start();
      await harness.client.execute({ type: "SubmitText", text: "What does BESS mean?" });
      await waitFor(async () =>
        (await harness.client.getSnapshot()).feedItems.some((item) => item.kind === "answer"),
      );
      const runDirs = await readdir(runsRoot);
      const events = await readFile(join(runsRoot, runDirs[0]!, "events.jsonl"), "utf8");
      expect(events).not.toContain("Battery Energy Storage");
      expect(events).toMatch(/judgment\.(requested|completed)|policy\.evaluated|answer\.committed/);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 4000): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error("timeout");
}
