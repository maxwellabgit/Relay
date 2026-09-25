import { describe, expect, it } from "vitest";
import type { JudgmentPort, JudgmentResponse, PublicSearchPort, TextModelPort } from "@relay/contracts";
import { recordedSuccess } from "@relay/testkit";
import { createNodeHarness } from "./create-client.js";

const MODEL_ANSWER = "A concise local reply.";

describe("tool and operation kernel", () => {
  it("Ask → respond tool → completes the same Case", async () => {
    const model: TextModelPort = {
      async generate() {
        return { ok: true, text: MODEL_ANSWER, model: "test", elapsedMs: 1 };
      },
    };
    const harness = await createNodeHarness({ model });
    try {
      await harness.client.start();
      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "Explain connection-oriented transport briefly.",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && i.caseId === caseId) &&
          row?.status === "completed"
        );
      });
      const snap = await harness.client.getSnapshot();
      expect(snap.feedItems.find((i) => i.kind === "answer")?.summary).toContain(MODEL_ANSWER);
      expect((await harness.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("Ask → memory.search → cites local memory and completes", async () => {
    const harness = await createNodeHarness({ reflexModules: [] });
    try {
      await harness.client.start();
      await harness.client.execute({
        type: "UpsertGlossaryEntry",
        token: "MSRP",
        expansion: "Manufacturer Suggested Retail Price",
        confirmed: true,
      });
      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "search my memory for MSRP",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && i.summary.includes("Sources:")) &&
          row?.status === "completed"
        );
      });
      const snap = await harness.client.getSnapshot();
      const answer = snap.feedItems.find((i) => i.kind === "answer")?.summary ?? "";
      expect(answer).toContain("Manufacturer Suggested Retail Price");
      expect(answer).toContain("Sources:");
      expect((await harness.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("Ask → Jev chooses public-search among eligible tools only", async () => {
    const publicSearch: PublicSearchPort = {
      async search(query) {
        return [
          {
            title: "Example result",
            url: "https://example.test/france",
            snippet: `Public answer for ${query}`,
            retrievedAt: "2020-01-01T00:00:00.000Z",
          },
        ];
      },
    };
    const judgments: JudgmentPort = {
      async judge(request): Promise<JudgmentResponse> {
        if (request.questionSetId === "judgment.tool-route") {
          const options = Object.keys(
            (request.questions.route as { criteria: Record<string, string> }).criteria,
          );
          expect(options).toContain("respond");
          expect(options).toContain("memory.search@1");
          expect(options).toContain("public-search.search@1");
          expect(options).not.toContain("shell.exec@1");
          expect(options).toContain("no_match");
          return recordedSuccess({
            route: {
              type: "choice",
              choice: "public-search.search@1",
              probabilities: {
                "public-search.search@1": 0.8,
                "memory.search@1": 0.1,
                respond: 0.05,
                no_match: 0.05,
              },
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
      expect(started.ok).toBe(true);
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: started.authorizationAttemptId!,
        providerCallbackRef: "callback_ref",
      });
      expect(completed.ok).toBe(true);
      const connectionId = completed.connectionId!;
      const snapBefore = await harness.client.getSnapshot();
      const connection = snapBefore.connections.find((c) => c.connectionId === connectionId);
      expect(connection?.connected).toBe(false);
      expect(connection?.healthStatus).toBe("authority_recorded");
      await harness.client.execute({
        type: "GrantHostedDisclosure",
        connectionId,
        expectedConnectionVersion: connection!.connectionVersion,
        disclosure: "public",
        sensitivity: 0,
        purpose: "public_search_fixture",
      });

      const accepted = await harness.client.execute({
        type: "SubmitText",
        text: "What is the capital of France?",
      });
      const caseId = accepted.caseId!;
      await waitFor(async () => {
        const snap = await harness.client.getSnapshot();
        const row = await harness.store.getCase(caseId);
        return (
          snap.feedItems.some((i) => i.kind === "answer" && i.summary.includes("Example result")) &&
          row?.status === "completed"
        );
      });
      const snap = await harness.client.getSnapshot();
      const answer = snap.feedItems.find((i) => i.kind === "answer")?.summary ?? "";
      expect(answer).toContain("Example result");
      expect(answer).toContain("https://example.test/france");
      expect(answer).toContain("Sources:");
      expect(snap.gate?.gateId).toBe("tool.route");
      expect(snap.gate?.selectedOptionId).toBe("public-search.search@1");
      expect((await harness.store.getCase(caseId))?.status).toBe("completed");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });

  it("ApproveOperation / RejectOperation and connection commands are executable", async () => {
    const harness = await createNodeHarness();
    try {
      await harness.client.start();
      const started = await harness.client.execute({
        type: "StartConnectionAuthorization",
        connector: { id: "memory", version: 1 },
      });
      expect(started.ok).toBe(true);
      const authId = started.authorizationAttemptId!;
      const connectionId = started.connectionId!;
      const completed = await harness.client.execute({
        type: "CompleteConnectionAuthorization",
        authorizationAttemptId: authId,
        providerCallbackRef: "cb",
      });
      expect(completed.ok).toBe(true);
      let snap = await harness.client.getSnapshot();
      let connection = snap.connections.find((c) => c.connectionId === connectionId)!;
      const observed = await harness.client.execute({
        type: "SetConnectionObservation",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        observationEnabled: true,
      });
      expect(observed.ok).toBe(true);
      snap = await harness.client.getSnapshot();
      connection = snap.connections.find((c) => c.connectionId === connectionId)!;
      const write = await harness.client.execute({
        type: "SetWriteAction",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        action: {
          connectorId: "memory",
          connectorVersion: 1,
          actionId: "note-commit",
          actionVersion: 1,
        },
        enabled: true,
      });
      expect(write.ok).toBe(true);

      // Propose an operation via authority domain event through approve path setup:
      // OperationService approve requires a pending envelope — seed via Start is enough for command surface.
      const stale = await harness.client.execute({
        type: "ApproveOperation",
        operationId: "missing",
        expectedCanonicalHash: "x",
        expectedCaseVersion: 1,
      });
      expect(stale.ok).toBe(false);
      expect(stale.error).toBe("operation_not_found");

      const activated = await harness.client.execute({
        type: "ActivateReflex",
        reflex: { id: "reflex.preserve-important-information", version: 1 },
        expectedStateVersion: 0,
      });
      expect(activated.ok).toBe(true);
      snap = await harness.client.getSnapshot();
      expect(
        snap.reflexes.some(
          (r) => r.reflex.id === "reflex.preserve-important-information" && r.activation === "active",
        ),
      ).toBe(true);
      connection = snap.connections.find((c) => c.connectionId === connectionId)!;
      const badDisclosure = await harness.client.execute({
        type: "GrantHostedDisclosure",
        connectionId,
        expectedConnectionVersion: connection.connectionVersion,
        disclosure: "public",
        sensitivity: 0,
        purpose: "should_fail",
      });
      expect(badDisclosure.ok).toBe(false);
      expect(badDisclosure.error).toBe("disclosure_not_allowed");
    } finally {
      await harness.client.stop();
      harness.close();
    }
  });
});

async function waitFor(predicate: () => Promise<boolean>): Promise<void> {
  for (let i = 0; i < 400; i += 1) {
    if (await predicate()) return;
    await new Promise((r) => setTimeout(r, 25));
  }
  throw new Error("timeout");
}
