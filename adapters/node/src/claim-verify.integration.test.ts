import { describe, expect, it } from "vitest";
import type { GitHubReadPort, JudgmentPort, JudgmentResponse, PublicSearchPort } from "@relay/contracts";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

describe("claim verification and GitHub read", () => {
  it("Lightshift claim → supported with public-search citations", async () => {
    const publicSearch: PublicSearchPort = {
      async search() {
        return [
          {
            title: "Lightshift storage footprint",
            url: "https://search.test/lightshift-sites",
            snippet: "Lightshift operates 20 battery energy storage sites across the UK.",
            retrievedAt: "2020-01-01T00:00:00.000Z",
          },
        ];
      },
    };
    const judgments: JudgmentPort = {
      async judge(request): Promise<JudgmentResponse> {
        if (request.questionSetId === "judgment.claim-source-choice") {
          return recordedSuccess({
            source: {
              type: "choice",
              choice: "public_search",
              probabilities: { public_search: 0.85, local_memory: 0.1, no_match: 0.05 },
              confidence: 0.85,
            },
          });
        }
        if (request.questionSetId === "judgment.claim-support") {
          return recordedSuccess({
            support: {
              type: "choice",
              choice: "supported",
              probabilities: { supported: 0.9, contradicted: 0.05, insufficient: 0.05 },
              confidence: 0.9,
            },
          });
        }
        return { ok: false, failure: { category: "disabled", message: "unexpected" } };
      },
    };
    const harness = await createNodeHarness({ judgments, publicSearch });
    try {
      await harness.client.start();
      const started = await harness.client.execute({
        type: "StartConnectionAuthorization",
        connector: { id: "public-search", version: 1 },
      });
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: started.authorizationAttemptId!,
        providerCallbackRef: "cb",
      });
      const connectionId = completed.connectionId!;
      const snap0 = await harness.client.getSnapshot();
      const connection = snap0.connections.find((c) => c.connectionId === connectionId)!;
      await harness.client.execute({
        type: "GrantHostedDisclosure",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        disclosure: "public",
        sensitivity: 0,
        purpose: "claim_fixture",
      });

      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "Verify: Lightshift operates 20 battery energy storage sites.",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && /Supported/i.test(i.summary)) &&
          row?.status === "completed"
        );
      });
      const snap = await harness.client.getSnapshot();
      const answer = snap.feedItems.find((i) => i.kind === "answer")?.summary ?? "";
      expect(answer).toMatch(/Supported/i);
      expect(answer).toContain("Lightshift");
      expect(answer).toContain("Sources:");
      expect(answer).toContain("https://search.test/lightshift-sites");
      expect((await harness.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("claim with no evidence returns honest insufficient", async () => {
    const publicSearch: PublicSearchPort = {
      async search() {
        return [];
      },
    };
    const judgments: JudgmentPort = {
      async judge(request): Promise<JudgmentResponse> {
        if (request.questionSetId === "judgment.claim-source-choice") {
          return recordedSuccess({
            source: {
              type: "choice",
              choice: "public_search",
              probabilities: { public_search: 0.8, local_memory: 0.15, no_match: 0.05 },
              confidence: 0.8,
            },
          });
        }
        return { ok: false, failure: { category: "disabled", message: "unexpected" } };
      },
    };
    const harness = await createNodeHarness({ judgments, publicSearch });
    try {
      await harness.client.start();
      const started = await harness.client.execute({
        type: "StartConnectionAuthorization",
        connector: { id: "public-search", version: 1 },
      });
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: started.authorizationAttemptId!,
        providerCallbackRef: "cb",
      });
      const connectionId = completed.connectionId!;
      const snap0 = await harness.client.getSnapshot();
      const connection = snap0.connections.find((c) => c.connectionId === connectionId)!;
      await harness.client.execute({
        type: "GrantHostedDisclosure",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        disclosure: "public",
        sensitivity: 0,
        purpose: "claim_empty",
      });

      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "Verify: AcmeCorp invented the moon.",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && /Insufficient/i.test(i.summary)) &&
          row?.status === "completed"
        );
      });
      const answer =
        (await harness.client.getSnapshot()).feedItems.find((i) => i.kind === "answer")?.summary ?? "";
      expect(answer).toMatch(/Insufficient/i);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("public-search outage leaves claim Case recoverably blocked", async () => {
    const publicSearch: PublicSearchPort = {
      async search() {
        throw new Error("provider_outage");
      },
    };
    const judgments: JudgmentPort = {
      async judge(request): Promise<JudgmentResponse> {
        if (request.questionSetId === "judgment.claim-source-choice") {
          return recordedSuccess({
            source: {
              type: "choice",
              choice: "public_search",
              probabilities: { public_search: 0.9, local_memory: 0.05, no_match: 0.05 },
              confidence: 0.9,
            },
          });
        }
        return { ok: false, failure: { category: "disabled", message: "unexpected" } };
      },
    };
    const harness = await createNodeHarness({ judgments, publicSearch });
    try {
      await harness.client.start();
      const started = await harness.client.execute({
        type: "StartConnectionAuthorization",
        connector: { id: "public-search", version: 1 },
      });
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: started.authorizationAttemptId!,
        providerCallbackRef: "cb",
      });
      const connectionId = completed.connectionId!;
      const snap0 = await harness.client.getSnapshot();
      const connection = snap0.connections.find((c) => c.connectionId === connectionId)!;
      await harness.client.execute({
        type: "GrantHostedDisclosure",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        disclosure: "public",
        sensitivity: 0,
        purpose: "outage",
      });

      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "Verify: Lightshift operates 20 battery energy storage sites.",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "wait" && /outage|Source/i.test(i.summary)) &&
          row?.status === "blocked"
        );
      });
      expect((await harness.store.getCase(caseId))?.status).toBe("blocked");
      // Re-submit starts a new Case — outage does not leave a stuck waiting Case.
      const retry = await harness.client.execute({
        type: "SubmitText",
        text: "Verify: Lightshift operates 20 battery energy storage sites.",
      });
      expect(retry.caseId).toBeTruthy();
      expect(retry.caseId).not.toBe(caseId);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("GitHub read works when connected; revoked scope blocks github.search", async () => {
    const github: GitHubReadPort = {
      async search(query) {
        return [
          {
            title: "relay#42",
            url: "https://github.com/example/relay/issues/42",
            snippet: `Issue about ${query}`,
            kind: "issue",
            retrievedAt: "2020-01-01T00:00:00.000Z",
          },
        ];
      },
    };
    const harness = await createNodeHarness({ github });
    try {
      await harness.client.start();
      const started = await harness.client.execute({
        type: "StartConnectionAuthorization",
        connector: { id: "github", version: 1 },
      });
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: started.authorizationAttemptId!,
        providerCallbackRef: "gh_cb",
      });
      const connectionId = completed.connectionId!;

      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "Search GitHub for claim budgets",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && i.summary.includes("relay#42")) &&
          row?.status === "completed"
        );
      });
      const answer =
        (await harness.client.getSnapshot()).feedItems.find((i) => i.kind === "answer")?.summary ?? "";
      expect(answer).toContain("relay#42");
      expect(answer).toContain("Sources:");

      const snap = await harness.client.getSnapshot();
      const connection = snap.connections.find((c) => c.connectionId === connectionId)!;
      const revoked = await harness.client.execute({
        type: "DisconnectConnection",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
      });
      expect(revoked.ok).toBe(true);

      const blocked = await harness.client.execute({
        type: "SubmitText",
        text: "Search GitHub for revoked scopes",
      });
      const blockedCaseId = blocked.caseId!;
      await waitFor(async () => {
        const row = await harness.store.getCase(blockedCaseId);
        return row?.status === "completed" || row?.status === "blocked" || row?.status === "failed";
      });
      const blockedSnap = await harness.client.getSnapshot();
      const blockedAnswer = blockedSnap.feedItems.filter((i) => i.caseId === blockedCaseId);
      // Without github, route falls back to respond (model disabled) — never reuses prior github hits.
      expect(blockedAnswer.length).toBeGreaterThan(0);
      const githubStillEligible = blockedSnap.feedItems.some(
        (i) => i.caseId === blockedCaseId && i.summary.includes("relay#42"),
      );
      expect(githubStillEligible).toBe(false);
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});

async function waitFor(predicate: () => Promise<boolean>, timeoutMs = 8_000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 40));
  }
  throw new Error("timeout");
}
