import { describe, expect, it } from "vitest";
import type { JudgmentQuestion } from "@relay/contracts";
import {
  JEV_MODEL,
  createTypeSafeJudgmentPort,
  parseTypeSafeBody,
  resolveProviderModel,
  retryDelayMs,
  wireTypeSafeBody,
} from "./typesafe-judgment.js";

const noulQuestion: JudgmentQuestion = {
  type: "noul",
  instructions: "Should this acronym be stored?",
};

const request = {
  questionSetId: "judgment.remember",
  questionSetVersion: "1",
  model: "typesafe",
  state: { token: "MSRP" },
  questions: { remember: noulQuestion },
};

const choiceQuestions = {
  department: {
    type: "choice" as const,
    instructions: "Which team?",
    criteria: { billing: "Payments", technical: "Bugs", sales: "Pricing" },
  },
};

const scoreQuestions = {
  frustration: {
    type: "score" as const,
    instructions: "How frustrated?",
    criteria: ["Calm", "Frustrated", "Very angry"],
  },
};

describe("TypeSafe Jev client", () => {
  it("defaults the provider model to jev-latest and omits local question-set fields", () => {
    expect(resolveProviderModel("typesafe")).toBe(JEV_MODEL);
    expect(resolveProviderModel("")).toBe(JEV_MODEL);
    expect(resolveProviderModel("jev-1.13.0")).toBe("jev-1.13.0");
    const body = wireTypeSafeBody(request);
    expect(body.model).toBe("jev-latest");
    expect(body.state).toEqual({ token: "MSRP" });
    expect(Object.keys(body).sort()).toEqual(["model", "questions", "state"]);
  });

  it("does not call the network when the API key is missing", async () => {
    let called = false;
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => null,
      fetchImpl: async () => {
        called = true;
        throw new Error("should not fetch");
      },
    });
    const result = await port.judge(request, new AbortController().signal);
    expect(called).toBe(false);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.failure.category).toBe("missing_secret");
  });

  it("posts only state, model, and questions and maps a noul probability", async () => {
    let seenUrl = "";
    let seenAuth = "";
    let seenBody = "";
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 1,
      fetchImpl: async (url, init) => {
        seenUrl = String(url);
        seenAuth = new Headers(init?.headers).get("Authorization") ?? "";
        seenBody = String(init?.body ?? "");
        return new Response(
          JSON.stringify({
            model: "jev-1.13.0",
            id: "req_noul",
            answers: { remember: { type: "noul", noul: 0.91 } },
            usage: { input_tokens: 12, output_tokens: 1 },
          }),
          { status: 200, headers: { "x-request-id": "hdr_noul" } },
        );
      },
    });

    const result = await port.judge(request, new AbortController().signal);
    expect(seenUrl).toBe("https://api.typesafe.ai/v1/systemone");
    expect(seenAuth).toBe("Bearer test-key");
    const posted = JSON.parse(seenBody) as Record<string, unknown>;
    expect(Object.keys(posted).sort()).toEqual(["model", "questions", "state"]);
    expect(posted.model).toBe("jev-latest");
    expect(seenBody).not.toContain("test-key");
    expect(seenBody).not.toContain("questionSetId");
    expect(result.ok).toBe(true);
    if (result.ok) {
      const answer = result.success.answers.remember;
      expect(answer?.type).toBe("noul");
      if (answer?.type === "noul") expect(answer.probabilityYes).toBe(0.91);
      expect(result.success.providerRequestId).toBe("hdr_noul");
    }
  });

  it("accepts choice and score fixtures and keeps a body request id when the header is absent", () => {
    const choice = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-1.13.0",
        request_id: "req_choice",
        answers: {
          department: {
            type: "choice",
            choice: "billing",
            probabilities: { billing: 0.88, technical: 0.12, sales: 0 },
            confidence: 0.81,
          },
        },
      }),
      4,
      choiceQuestions,
    );
    expect(choice.ok).toBe(true);
    if (choice.ok) expect(choice.success.providerRequestId).toBe("req_choice");

    const score = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-1.13.0",
        answers: {
          frustration: {
            type: "score",
            score: 1.05,
            legend: { "0": "Calm", "1": "Frustrated", "2": "Very angry" },
            probabilities: { "0": 0, "1": 0.95, "2": 0.05 },
            confidence: 0.92,
          },
        },
      }),
      4,
      scoreQuestions,
    );
    expect(score.ok).toBe(true);
  });

  it("fails closed on malformed, missing, extra, and wrong-kind outputs", () => {
    expect(
      parseTypeSafeBody(
        JSON.stringify({ model: "jev-1.13.0", answers: { remember: { type: "noul", noul: 2 } } }),
        1,
        { remember: noulQuestion },
      ).ok,
    ).toBe(false);
    expect(
      parseTypeSafeBody(JSON.stringify({ model: "jev-1.13.0", answers: {} }), 1, {
        remember: noulQuestion,
      }).ok,
    ).toBe(false);
    expect(
      parseTypeSafeBody(
        JSON.stringify({
          model: "jev-1.13.0",
          answers: {
            remember: { type: "noul", noul: 0.2 },
            extra: { type: "noul", noul: 0.2 },
          },
        }),
        1,
        { remember: noulQuestion },
      ).ok,
    ).toBe(false);
    expect(
      parseTypeSafeBody(
        JSON.stringify({
          model: "jev-1.13.0",
          answers: { remember: { type: "choice", choice: "a", probabilities: { a: 1 }, confidence: 1 } },
        }),
        1,
        { remember: noulQuestion },
      ).ok,
    ).toBe(false);
    expect(parseTypeSafeBody('{"model":"jev-1.13.0","answers":{"remember":{"type":"noul","noul":0.5},"remember":{"type":"noul","noul":0.6}}}', 1).ok).toBe(
      false,
    );
  });

  it("rejects a choice outside the supplied options and a bad probability sum", () => {
    const badOption = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-1.13.0",
        answers: {
          department: {
            type: "choice",
            choice: "legal",
            probabilities: { billing: 0.5, technical: 0.5, sales: 0 },
            confidence: 0.5,
          },
        },
      }),
      1,
      choiceQuestions,
    );
    expect(badOption.ok).toBe(false);
    const badSum = parseTypeSafeBody(
      JSON.stringify({
        model: "jev-1.13.0",
        answers: {
          department: {
            type: "choice",
            choice: "billing",
            probabilities: { billing: 0.5, technical: 0.1, sales: 0 },
            confidence: 0.5,
          },
        },
      }),
      1,
      choiceQuestions,
    );
    expect(badSum.ok).toBe(false);
  });

  it("does not retry 401, 402, or 422", async () => {
    for (const status of [401, 402, 422]) {
      let calls = 0;
      const port = createTypeSafeJudgmentPort({
        getApiKey: () => "test-key",
        random: () => 0,
        sleep: async () => {
          throw new Error("should not sleep");
        },
        fetchImpl: async () => {
          calls += 1;
          return new Response("{}", { status });
        },
      });
      const result = await port.judge(request, new AbortController().signal);
      expect(calls).toBe(1);
      expect(result.ok).toBe(false);
    }
  });

  it("honors Retry-After and otherwise uses exponential backoff with fake time", async () => {
    const slept: number[] = [];
    let calls = 0;
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 3,
      random: () => 0,
      now: () => 1_000,
      sleep: async (ms) => {
        slept.push(ms);
      },
      fetchImpl: async () => {
        calls += 1;
        if (calls === 1) return new Response("{}", { status: 429, headers: { "retry-after": "2" } });
        if (calls === 2) return new Response("{}", { status: 529 });
        return new Response(
          JSON.stringify({ model: "jev-latest", answers: { remember: { type: "noul", noul: 0.4 } } }),
          { status: 200 },
        );
      },
    });
    const result = await port.judge(request, new AbortController().signal);
    expect(result.ok).toBe(true);
    expect(slept).toEqual([2_000, 500]);
    expect(retryDelayMs(0, null, () => 0, 0)).toBe(250);
    expect(retryDelayMs(2, null, () => 1, 0)).toBe(1_200);
  });

  it("retries network loss and stops when the caller cancels", async () => {
    let calls = 0;
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      maxAttempts: 3,
      random: () => 0,
      sleep: async () => undefined,
      fetchImpl: async () => {
        calls += 1;
        throw new TypeError("failed to fetch");
      },
    });
    const lost = await port.judge(request, new AbortController().signal);
    expect(calls).toBe(3);
    expect(lost.ok).toBe(false);
    if (!lost.ok) expect(lost.failure.category).toBe("network");

    const controller = new AbortController();
    const cancelling = createTypeSafeJudgmentPort({
      getApiKey: () => "test-key",
      sleep: async () => undefined,
      fetchImpl: async () => {
        controller.abort();
        throw new DOMException("aborted", "AbortError");
      },
    });
    const cancelled = await cancelling.judge(request, controller.signal);
    expect(cancelled.ok).toBe(false);
    if (!cancelled.ok) expect(cancelled.failure.category).toBe("cancelled");
  });

  it("does not put the API key into a failure message", async () => {
    const port = createTypeSafeJudgmentPort({
      getApiKey: () => "super-secret-key",
      maxAttempts: 1,
      fetchImpl: async () => new Response("nope", { status: 500 }),
    });
    const result = await port.judge(request, new AbortController().signal);
    expect(JSON.stringify(result)).not.toContain("super-secret-key");
  });
});
