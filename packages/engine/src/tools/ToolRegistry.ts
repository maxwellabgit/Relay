import type {
  ToolBudgets,
  ToolDefinition,
  ToolJsonSchema,
  ToolResultEnvelope,
} from "@relay/contracts";
import { DEFAULT_TOOL_BUDGETS as DEFAULTS } from "@relay/contracts";
import { encodeText, sha256Hex } from "../engine-helpers.js";

export type RegisteredTool = {
  readonly definition: ToolDefinition;
  readonly execute: (args: Readonly<Record<string, unknown>>, signal: AbortSignal) => Promise<ToolResultEnvelope>;
};

export class ToolRegistry {
  private readonly tools = new Map<string, RegisteredTool>();

  register(tool: RegisteredTool): void {
    if (this.tools.has(tool.definition.id)) {
      throw new Error(`duplicate_tool:${tool.definition.id}`);
    }
    this.tools.set(tool.definition.id, tool);
  }

  get(id: string): RegisteredTool | undefined {
    return this.tools.get(id);
  }

  list(): readonly RegisteredTool[] {
    return [...this.tools.values()];
  }

  definitions(): readonly ToolDefinition[] {
    return this.list().map((tool) => tool.definition);
  }
}

export type EligibilityContext = {
  readonly caseOrigin: "direct" | "observed" | "dialogue";
  readonly remaining: ToolBudgets;
  readonly connectedConnectorIds: ReadonlySet<string>;
  readonly grantedScopes: ReadonlySet<string>;
  readonly hasPublicDisclosure: boolean;
  readonly publicSearchAvailable: boolean;
};

/** Code owns eligibility — Jev never sees unapproved tools. */
export function filterEligibleTools(
  definitions: readonly ToolDefinition[],
  context: EligibilityContext,
): readonly ToolDefinition[] {
  if (context.remaining.maxToolSteps <= 0) return [];
  return definitions.filter((tool) => {
    if (tool.effect === "external_write") return false;
    for (const scope of tool.requiredScopes) {
      if (!context.grantedScopes.has(scope) && !scopeSatisfiedByConnection(scope, context)) {
        return false;
      }
    }
    if (tool.disclosure === "public") {
      if (!context.publicSearchAvailable) return false;
      if (!context.hasPublicDisclosure || !context.connectedConnectorIds.has("public-search")) {
        return false;
      }
    }
    if (tool.id.startsWith("memory.") && context.caseOrigin === "observed") {
      // Ambient memory reads are Phase 5; Phase 4 keeps them on direct Asks.
      return false;
    }
    return true;
  });
}

function scopeSatisfiedByConnection(scope: string, context: EligibilityContext): boolean {
  if (scope === "memory.read") return true;
  if (scope === "public-search.read") {
    return context.connectedConnectorIds.has("public-search") && context.hasPublicDisclosure;
  }
  if (scope === "assistant.respond") return true;
  return context.grantedScopes.has(scope);
}

export function validateToolArgs(
  schema: ToolJsonSchema,
  args: unknown,
): { ok: true; value: Record<string, unknown> } | { ok: false; error: string } {
  if (!args || typeof args !== "object" || Array.isArray(args)) {
    return { ok: false, error: "args_not_object" };
  }
  const record = args as Record<string, unknown>;
  if (schema.additionalProperties === false) {
    for (const key of Object.keys(record)) {
      if (!(key in schema.properties)) return { ok: false, error: `unexpected_property:${key}` };
    }
  }
  for (const key of schema.required ?? []) {
    if (!(key in record)) return { ok: false, error: `missing_property:${key}` };
  }
  for (const [key, prop] of Object.entries(schema.properties)) {
    if (!(key in record)) continue;
    const value = record[key];
    if (prop.type === "string" && typeof value !== "string") return { ok: false, error: `type:${key}` };
    if (prop.type === "number" && typeof value !== "number") return { ok: false, error: `type:${key}` };
    if (prop.type === "boolean" && typeof value !== "boolean") return { ok: false, error: `type:${key}` };
  }
  return { ok: true, value: { ...record } };
}

/** Stable canonical JSON for hashes and idempotency. */
export function canonicalizeArgs(value: unknown): string {
  return JSON.stringify(sortValue(value));
}

export async function hashCanonicalArgs(value: unknown): Promise<string> {
  return sha256Hex(encodeText(canonicalizeArgs(value)));
}

function sortValue(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortValue);
  if (value && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>).sort(([a], [b]) =>
      a < b ? -1 : a > b ? 1 : 0,
    );
    const out: Record<string, unknown> = {};
    for (const [key, item] of entries) out[key] = sortValue(item);
    return out;
  }
  return value;
}

export function remainingBudgets(
  used: Partial<ToolBudgets>,
  caps: ToolBudgets = DEFAULTS,
): ToolBudgets {
  return {
    maxToolSteps: Math.max(0, caps.maxToolSteps - (used.maxToolSteps ?? 0)),
    maxJudgmentRounds: Math.max(0, caps.maxJudgmentRounds - (used.maxJudgmentRounds ?? 0)),
    maxSourceAttempts: Math.max(0, caps.maxSourceAttempts - (used.maxSourceAttempts ?? 0)),
  };
}

export { DEFAULTS as DEFAULT_TOOL_BUDGETS };
