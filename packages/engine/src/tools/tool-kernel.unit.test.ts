import { describe, expect, it } from "vitest";
import { DEFAULT_TOOL_BUDGETS } from "@relay/contracts";
import {
  canonicalizeArgs,
  filterEligibleTools,
  remainingBudgets,
  validateToolArgs,
} from "./ToolRegistry.js";
import { TOOL_CLAIM_VERIFY, TOOL_GITHUB_SEARCH, TOOL_MEMORY_SEARCH, TOOL_PUBLIC_SEARCH, TOOL_RESPOND } from "./builtins.js";

const defs = [
  {
    id: TOOL_RESPOND,
    description: "respond",
    inputSchema: {
      type: "object" as const,
      properties: { text: { type: "string" as const } },
      required: ["text"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object" as const,
      properties: { answer: { type: "string" as const } },
      required: ["answer"],
    },
    effect: "read" as const,
    disclosure: "local_only" as const,
    requiredScopes: ["assistant.respond"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
  {
    id: TOOL_MEMORY_SEARCH,
    description: "memory",
    inputSchema: {
      type: "object" as const,
      properties: { query: { type: "string" as const } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object" as const,
      properties: { hitCount: { type: "number" as const } },
      required: ["hitCount"],
    },
    effect: "read" as const,
    disclosure: "local_only" as const,
    requiredScopes: ["memory.read"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
  {
    id: TOOL_PUBLIC_SEARCH,
    description: "public",
    inputSchema: {
      type: "object" as const,
      properties: { query: { type: "string" as const } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object" as const,
      properties: { hitCount: { type: "number" as const } },
      required: ["hitCount"],
    },
    effect: "read" as const,
    disclosure: "public" as const,
    requiredScopes: ["public-search.read"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
  {
    id: TOOL_CLAIM_VERIFY,
    description: "claim",
    inputSchema: {
      type: "object" as const,
      properties: { claim: { type: "string" as const } },
      required: ["claim"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object" as const,
      properties: { verdict: { type: "string" as const } },
      required: ["verdict"],
    },
    effect: "read" as const,
    disclosure: "hosted_allowed" as const,
    requiredScopes: ["claim.verify"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
  {
    id: TOOL_GITHUB_SEARCH,
    description: "github",
    inputSchema: {
      type: "object" as const,
      properties: { query: { type: "string" as const } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object" as const,
      properties: { hitCount: { type: "number" as const } },
      required: ["hitCount"],
    },
    effect: "read" as const,
    disclosure: "local_only" as const,
    requiredScopes: ["github.read"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
  {
    id: "shell.exec@1",
    description: "forbidden",
    inputSchema: {
      type: "object" as const,
      properties: { cmd: { type: "string" as const } },
      required: ["cmd"],
    },
    outputSchema: {
      type: "object" as const,
      properties: { out: { type: "string" as const } },
      required: ["out"],
    },
    effect: "external_write" as const,
    disclosure: "local_only" as const,
    requiredScopes: ["shell"],
    timeoutMs: 1,
    retryPolicy: { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 },
  },
];

describe("tool eligibility", () => {
  it("never surfaces unapproved or external-write tools", () => {
    const eligible = filterEligibleTools(defs, {
      caseOrigin: "direct",
      remaining: DEFAULT_TOOL_BUDGETS,
      connectedConnectorIds: new Set(),
      grantedScopes: new Set(),
      hasPublicDisclosure: false,
      publicSearchAvailable: false,
    });
    const ids = eligible.map((tool) => tool.id);
    expect(ids).toContain(TOOL_RESPOND);
    expect(ids).toContain(TOOL_MEMORY_SEARCH);
    expect(ids).not.toContain(TOOL_PUBLIC_SEARCH);
    expect(ids).not.toContain("shell.exec@1");
  });

  it("includes public-search only when connected and disclosed", () => {
    const eligible = filterEligibleTools(defs, {
      caseOrigin: "direct",
      remaining: DEFAULT_TOOL_BUDGETS,
      connectedConnectorIds: new Set(["public-search"]),
      grantedScopes: new Set(),
      hasPublicDisclosure: true,
      publicSearchAvailable: true,
    });
    expect(eligible.map((tool) => tool.id)).toContain(TOOL_PUBLIC_SEARCH);
  });

  it("excludes public-search when only a non-search disclosure exists", () => {
    const eligible = filterEligibleTools(defs, {
      caseOrigin: "direct",
      remaining: DEFAULT_TOOL_BUDGETS,
      connectedConnectorIds: new Set(["calendar"]),
      grantedScopes: new Set(),
      hasPublicDisclosure: true,
      publicSearchAvailable: false,
    });
    expect(eligible.map((tool) => tool.id)).not.toContain(TOOL_PUBLIC_SEARCH);
  });

  it("includes claim.verify when available and github.search only when connected", () => {
    const without = filterEligibleTools(defs, {
      caseOrigin: "direct",
      remaining: DEFAULT_TOOL_BUDGETS,
      connectedConnectorIds: new Set(),
      grantedScopes: new Set(),
      hasPublicDisclosure: false,
      publicSearchAvailable: false,
      claimVerifyAvailable: true,
      githubAvailable: false,
    });
    expect(without.map((t) => t.id)).toContain(TOOL_CLAIM_VERIFY);
    expect(without.map((t) => t.id)).not.toContain(TOOL_GITHUB_SEARCH);

    const withGithub = filterEligibleTools(defs, {
      caseOrigin: "direct",
      remaining: DEFAULT_TOOL_BUDGETS,
      connectedConnectorIds: new Set(["github"]),
      grantedScopes: new Set(["github.read"]),
      hasPublicDisclosure: false,
      publicSearchAvailable: false,
      claimVerifyAvailable: true,
      githubAvailable: true,
    });
    expect(withGithub.map((t) => t.id)).toContain(TOOL_GITHUB_SEARCH);
  });
});

describe("tool args and budgets", () => {
  it("validates and canonicalizes arguments", () => {
    const schema = defs[1]!.inputSchema;
    expect(validateToolArgs(schema, { query: "msrp" })).toEqual({
      ok: true,
      value: { query: "msrp" },
    });
    expect(validateToolArgs(schema, { query: 1 }).ok).toBe(false);
    expect(canonicalizeArgs({ b: 1, a: 2 })).toBe('{"a":2,"b":1}');
  });

  it("enforces remaining budgets", () => {
    expect(
      remainingBudgets({ maxToolSteps: 3, maxJudgmentRounds: 2, maxSourceAttempts: 3 }),
    ).toEqual({ maxToolSteps: 0, maxJudgmentRounds: 0, maxSourceAttempts: 0 });
    expect(remainingBudgets({ maxToolSteps: 1 })).toEqual({
      maxToolSteps: 2,
      maxJudgmentRounds: 2,
      maxSourceAttempts: 3,
    });
  });
});
