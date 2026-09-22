import type {
  ArtifactStorePort,
  GitHubReadPort,
  PublicSearchPort,
  SourceSliceRef,
  TextModelPort,
  ToolCitation,
  ToolDefinition,
  ToolResultEnvelope,
} from "@relay/contracts";
import { localOnlyPolicy, publicPolicy } from "@relay/contracts";
import { DIRECT_ANSWER_PROMPT_V1 } from "../prompts/direct-answer.v1.js";
import type { LearningStore } from "../learning-store.js";
import { encodeText } from "../engine-helpers.js";
import type { RegisteredTool, ToolRegistry } from "./ToolRegistry.js";

export const TOOL_RESPOND = "assistant.respond@1";
export const TOOL_MEMORY_SEARCH = "memory.search@1";
export const TOOL_PUBLIC_SEARCH = "public-search.search@1";
export const TOOL_CLAIM_VERIFY = "claim.verify@1";
export const TOOL_GITHUB_SEARCH = "github.search@1";

const RETRY = { maxAttempts: 1, initialBackoffMs: 0, maxBackoffMs: 0 };

export function createBuiltinTools(deps: {
  readonly learning: LearningStore;
  readonly artifacts: ArtifactStorePort;
  readonly model: TextModelPort;
  readonly publicSearch?: PublicSearchPort;
  readonly github?: GitHubReadPort;
  readonly clock: { now(): Date };
}): readonly RegisteredTool[] {
  return [
    respondTool(deps),
    memorySearchTool(deps),
    publicSearchTool(deps),
    claimVerifyTool(),
    githubSearchTool(deps),
  ];
}

export function registerBuiltinTools(registry: ToolRegistry, deps: Parameters<typeof createBuiltinTools>[0]): void {
  for (const tool of createBuiltinTools(deps)) registry.register(tool);
}

function respondTool(deps: {
  readonly model: TextModelPort;
}): RegisteredTool {
  const definition: ToolDefinition = {
    id: TOOL_RESPOND,
    description: "Draft a local conversational response without external retrieval.",
    inputSchema: {
      type: "object",
      properties: { text: { type: "string" } },
      required: ["text"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { answer: { type: "string" } },
      required: ["answer"],
      additionalProperties: false,
    },
    effect: "read",
    disclosure: "local_only",
    requiredScopes: ["assistant.respond"],
    timeoutMs: 30_000,
    retryPolicy: RETRY,
  };
  return {
    definition,
    async execute(args, signal) {
      const text = String(args.text ?? "");
      const generated = await deps.model.generate(
        {
          taskKind: "direct_answer",
          promptVersion: DIRECT_ANSWER_PROMPT_V1.promptVersion,
          prompt: DIRECT_ANSWER_PROMPT_V1.build(text),
          maxTokens: 220,
          temperature: 0.2,
        },
        signal,
      );
      if (signal?.aborted) {
        return envelope(definition.id, "failed", "No local result for this Ask.", [], [], {
          reasonCode: "cancelled",
        });
      }
      if (!generated.ok) {
        return envelope(definition.id, "failed", "No local result for this Ask.", [], [], {
          reasonCode: generated.failureReason,
        });
      }
      return envelope(definition.id, "ok", generated.text, [], [], {
        output: { answer: generated.text },
      });
    },
  };
}

function memorySearchTool(deps: {
  readonly learning: LearningStore;
  readonly artifacts: ArtifactStorePort;
}): RegisteredTool {
  const definition: ToolDefinition = {
    id: TOOL_MEMORY_SEARCH,
    description: "Search local glossary and birthday memory with source refs.",
    inputSchema: {
      type: "object",
      properties: { query: { type: "string" } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { hitCount: { type: "number" } },
      required: ["hitCount"],
      additionalProperties: false,
    },
    effect: "read",
    disclosure: "local_only",
    requiredScopes: ["memory.read"],
    timeoutMs: 5_000,
    retryPolicy: RETRY,
  };
  return {
    definition,
    async execute(args) {
      const query = String(args.query ?? "").trim().toLowerCase();
      const memories = await deps.learning.listMemories();
      const hits = memories.filter((memory) => {
        const hay = `${memory.kind} ${memory.key} ${Object.values(memory.value).join(" ")}`.toLowerCase();
        return query.length === 0 ? false : hay.includes(query) || memory.key.toLowerCase() === query;
      });
      const citations: ToolCitation[] = [];
      const slices: SourceSliceRef[] = [];
      for (const hit of hits.slice(0, 5)) {
        const summary =
          hit.kind === "glossary"
            ? `${hit.key}: ${hit.value.expansion ?? ""}`
            : `${hit.value.displayName ?? hit.key} birthday ${hit.value.month ?? ""}-${hit.value.day ?? ""}`;
        const bytes = encodeText(summary);
        const ref = await deps.artifacts.put(bytes, localOnlyPolicy());
        const slice: SourceSliceRef = {
          artifactId: ref.artifactId,
          sha256: ref.sha256,
          start: 0,
          end: summary.length,
          offsetsValidated: true,
        };
        slices.push(slice);
        citations.push({ title: `memory:${hit.kind}:${hit.key}`, snippet: summary, sourceSlice: slice });
      }
      if (hits.length === 0) {
        return envelope(definition.id, "empty", "No local memory matches.", [], [], {
          output: { hitCount: 0 },
          reasonCode: "no_candidates",
        });
      }
      const summary = citations.map((c) => c.snippet).join(" · ");
      return envelope(definition.id, "ok", summary, citations, slices, {
        output: { hitCount: hits.length },
      });
    },
  };
}

function publicSearchTool(deps: {
  readonly artifacts: ArtifactStorePort;
  readonly publicSearch?: PublicSearchPort;
  readonly clock: { now(): Date };
}): RegisteredTool {
  const definition: ToolDefinition = {
    id: TOOL_PUBLIC_SEARCH,
    description: "Search the public web through the closed public-search adapter.",
    inputSchema: {
      type: "object",
      properties: { query: { type: "string" } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { hitCount: { type: "number" } },
      required: ["hitCount"],
      additionalProperties: false,
    },
    effect: "read",
    disclosure: "public",
    requiredScopes: ["public-search.read"],
    timeoutMs: 15_000,
    retryPolicy: RETRY,
  };
  return {
    definition,
    async execute(args, signal) {
      if (!deps.publicSearch) {
        return envelope(definition.id, "denied", "Public search is not configured.", [], [], {
          reasonCode: "not_authorized",
        });
      }
      const query = String(args.query ?? "").trim();
      if (!query) {
        return envelope(definition.id, "failed", "Empty search query.", [], [], {
          reasonCode: "invalid_response",
        });
      }
      const hits = await deps.publicSearch.search(query, signal);
      const citations: ToolCitation[] = [];
      const slices: SourceSliceRef[] = [];
      for (const hit of hits.slice(0, 5)) {
        const body = `${hit.title}\n${hit.url}\n${hit.snippet}`;
        const bytes = encodeText(body);
        const ref = await deps.artifacts.put(bytes, publicPolicy());
        const slice: SourceSliceRef = {
          artifactId: ref.artifactId,
          sha256: ref.sha256,
          start: 0,
          end: body.length,
          offsetsValidated: true,
        };
        slices.push(slice);
        citations.push({
          title: hit.title,
          url: hit.url,
          snippet: hit.snippet,
          sourceSlice: slice,
        });
      }
      if (hits.length === 0) {
        return envelope(definition.id, "empty", "No public-search results.", [], [], {
          output: { hitCount: 0 },
          reasonCode: "no_candidates",
        });
      }
      const summary = citations
        .map((c) => (c.url ? `${c.title} (${c.url})` : c.title))
        .join(" · ");
      return envelope(definition.id, "ok", summary, citations, slices, {
        output: { hitCount: hits.length },
      });
    },
  };
}

/** Definition only — ToolBroker runs ClaimVerifier with case context and eligible sources. */
function claimVerifyTool(): RegisteredTool {
  const definition: ToolDefinition = {
    id: TOOL_CLAIM_VERIFY,
    description: "Verify a factual claim over eligible local, public, and GitHub sources.",
    inputSchema: {
      type: "object",
      properties: { claim: { type: "string" } },
      required: ["claim"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { verdict: { type: "string" } },
      required: ["verdict"],
      additionalProperties: false,
    },
    effect: "read",
    disclosure: "hosted_allowed",
    requiredScopes: ["claim.verify"],
    timeoutMs: 60_000,
    retryPolicy: RETRY,
  };
  return {
    definition,
    async execute() {
      return envelope(definition.id, "failed", "Claim verify requires broker case context.", [], [], {
        reasonCode: "invalid_response",
      });
    },
  };
}

function githubSearchTool(deps: {
  readonly artifacts: ArtifactStorePort;
  readonly github?: GitHubReadPort;
}): RegisteredTool {
  const definition: ToolDefinition = {
    id: TOOL_GITHUB_SEARCH,
    description: "Search connected GitHub resources through the closed GitHub read adapter.",
    inputSchema: {
      type: "object",
      properties: { query: { type: "string" } },
      required: ["query"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { hitCount: { type: "number" } },
      required: ["hitCount"],
      additionalProperties: false,
    },
    effect: "read",
    disclosure: "local_only",
    requiredScopes: ["github.read"],
    timeoutMs: 15_000,
    retryPolicy: RETRY,
  };
  return {
    definition,
    async execute(args, signal) {
      if (!deps.github) {
        return envelope(definition.id, "denied", "GitHub read is not configured.", [], [], {
          reasonCode: "not_authorized",
        });
      }
      const query = String(args.query ?? "").trim();
      if (!query) {
        return envelope(definition.id, "failed", "Empty search query.", [], [], {
          reasonCode: "invalid_response",
        });
      }
      const hits = await deps.github.search(query, signal);
      const citations: ToolCitation[] = [];
      const slices: SourceSliceRef[] = [];
      for (const hit of hits.slice(0, 5)) {
        const body = `${hit.title}\n${hit.url}\n${hit.snippet}`;
        const bytes = encodeText(body);
        const ref = await deps.artifacts.put(bytes, localOnlyPolicy());
        const slice: SourceSliceRef = {
          artifactId: ref.artifactId,
          sha256: ref.sha256,
          start: 0,
          end: body.length,
          offsetsValidated: true,
        };
        slices.push(slice);
        citations.push({
          title: `github:${hit.kind}:${hit.title}`,
          url: hit.url,
          snippet: hit.snippet,
          sourceSlice: slice,
        });
      }
      if (hits.length === 0) {
        return envelope(definition.id, "empty", "No GitHub results.", [], [], {
          output: { hitCount: 0 },
          reasonCode: "no_candidates",
        });
      }
      const summary = citations.map((c) => (c.url ? `${c.title} (${c.url})` : c.title)).join(" · ");
      return envelope(definition.id, "ok", summary, citations, slices, {
        output: { hitCount: hits.length },
      });
    },
  };
}

function envelope(
  toolId: string,
  status: ToolResultEnvelope["status"],
  summary: string,
  citations: readonly ToolCitation[],
  sourceSlices: readonly SourceSliceRef[],
  extra: Partial<ToolResultEnvelope> = {},
): ToolResultEnvelope {
  return {
    toolId,
    status,
    summary,
    citations,
    sourceSlices,
    output: extra.output ?? {},
    ...(extra.reasonCode ? { reasonCode: extra.reasonCode } : {}),
    ...(extra.operationId ? { operationId: extra.operationId } : {}),
  };
}
