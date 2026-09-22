import type { SourceSliceRef } from "./transcript.js";

/** Closed effect classes — tools never invent HTTP/shell/fs/MCP paths. */
export type ToolEffect = "read" | "local_write" | "external_write";

export type ToolDisclosure = "local_only" | "hosted_allowed" | "public";

export type ToolRetryPolicy = {
  readonly maxAttempts: number;
  readonly initialBackoffMs: number;
  readonly maxBackoffMs: number;
};

/**
 * Minimal JSON Schema subset used by the tool kernel.
 * Full draft validators are intentionally avoided; code owns validation.
 */
export type ToolJsonSchema = {
  readonly type: "object";
  readonly properties: Readonly<Record<string, { readonly type: "string" | "number" | "boolean" }>>;
  readonly required?: readonly string[];
  readonly additionalProperties?: boolean;
};

export type ToolDefinition<I = unknown, O = unknown> = {
  readonly id: string;
  readonly description: string;
  readonly inputSchema: ToolJsonSchema;
  readonly outputSchema: ToolJsonSchema;
  readonly effect: ToolEffect;
  readonly disclosure: ToolDisclosure;
  readonly requiredScopes: readonly string[];
  readonly timeoutMs: number;
  readonly retryPolicy: ToolRetryPolicy;
  /** Phantom type markers for callers; runtime uses unknown. */
  readonly _input?: I;
  readonly _output?: O;
};

/** Deterministic step budgets enforced by the orchestrator, not by providers. */
export type ToolBudgets = {
  readonly maxToolSteps: number;
  readonly maxJudgmentRounds: number;
  readonly maxSourceAttempts: number;
};

export const DEFAULT_TOOL_BUDGETS: ToolBudgets = {
  maxToolSteps: 3,
  maxJudgmentRounds: 2,
  maxSourceAttempts: 3,
};

/** Built-in control options always offered beside eligible tools (when applicable). */
export const TOOL_CONTROL_OPTIONS = ["respond", "clarify", "wait", "no_match"] as const;
export type ToolControlOption = (typeof TOOL_CONTROL_OPTIONS)[number];

export type ToolCitation = {
  readonly title: string;
  readonly url?: string;
  readonly snippet?: string;
  readonly sourceSlice: SourceSliceRef;
};

export type ToolResultStatus = "ok" | "empty" | "denied" | "failed" | "needs_approval";

export type ToolResultEnvelope = {
  readonly toolId: string;
  readonly status: ToolResultStatus;
  readonly summary: string;
  readonly citations: readonly ToolCitation[];
  readonly sourceSlices: readonly SourceSliceRef[];
  readonly output: unknown;
  readonly reasonCode?: string;
  readonly operationId?: string;
};

export type PublicSearchHit = {
  readonly title: string;
  readonly url: string;
  readonly snippet: string;
  readonly retrievedAt: string;
};

/** Closed public-search adapter — engine never opens arbitrary HTTP. */
export type PublicSearchPort = {
  search(query: string, signal: AbortSignal): Promise<readonly PublicSearchHit[]>;
};

export type MemorySearchHit = {
  readonly kind: string;
  readonly key: string;
  readonly summary: string;
  readonly memoryId: string;
};
