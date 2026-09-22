import type {
  ArtifactStorePort,
  ClaimSourceId,
  ClaimVerdict,
  ClaimVerifyOutput,
  GitHubReadPort,
  JudgmentPort,
  PublicSearchPort,
  SourceSliceRef,
  ToolCitation,
  ToolResultEnvelope,
} from "@relay/contracts";
import { DEFAULT_TOOL_BUDGETS, localOnlyPolicy, publicPolicy } from "@relay/contracts";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import { evaluateChoiceGate, validateChoiceDistribution } from "../policies.js";
import type { LearningStore } from "../learning-store.js";
import { encodeText } from "../engine-helpers.js";
import { JEV_MODEL } from "../typesafe-judgment.js";
import { loadDisclosureGate } from "../disclosure/hosted-grant.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";

export const CLAIM_SOURCE_CHOICE_SET = "judgment.claim-source-choice";
export const CLAIM_SUPPORT_SET = "judgment.claim-support";

export type ClaimVerifierDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly learning: LearningStore;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly publicSearch?: PublicSearchPort;
  readonly github?: GitHubReadPort;
  readonly mode?: "live" | "recorded" | "replay";
  readonly sessionId: string;
};

export type ClaimEligibleSources = {
  readonly localMemory: boolean;
  readonly publicSearch: boolean;
  readonly github: boolean;
};

/**
 * Bounded claim verification: ≤3 source attempts, ≤2 post-retrieval judgment rounds.
 * Jev chooses only among code-supplied eligible sources and support labels.
 */
export class ClaimVerifier {
  constructor(private readonly deps: ClaimVerifierDeps) {}

  async verify(input: {
    readonly claim: string;
    readonly caseId: string;
    readonly caseVersion: number;
    readonly eligible: ClaimEligibleSources;
    readonly signal: AbortSignal;
  }): Promise<ToolResultEnvelope> {
    const claim = input.claim.trim();
    if (!claim) {
      return envelope("failed", "Empty claim.", [], [], {
        reasonCode: "invalid_response",
        output: { verdict: "insufficient" satisfies ClaimVerdict, claim: "", sourcesAttempted: [], judgmentRounds: 0 },
      });
    }

    const remaining = [
      ...(input.eligible.localMemory ? (["local_memory"] as const) : []),
      ...(input.eligible.publicSearch ? (["public_search"] as const) : []),
      ...(input.eligible.github ? (["github"] as const) : []),
    ] satisfies ClaimSourceId[];

    if (remaining.length === 0) {
      return envelope("denied", "No eligible sources for claim verification.", [], [], {
        reasonCode: "not_authorized",
        output: {
          verdict: "insufficient",
          claim,
          sourcesAttempted: [],
          judgmentRounds: 0,
        } satisfies ClaimVerifyOutput,
      });
    }

    const sourcesAttempted: ClaimSourceId[] = [];
    const allCitations: ToolCitation[] = [];
    const allSlices: SourceSliceRef[] = [];
    let judgmentRounds = 0;
    let sourceAttempts = 0;
    const pool = [...remaining];

    while (
      pool.length > 0 &&
      sourceAttempts < DEFAULT_TOOL_BUDGETS.maxSourceAttempts &&
      judgmentRounds < DEFAULT_TOOL_BUDGETS.maxJudgmentRounds
    ) {
      const source = await this.chooseSource(input.caseId, input.caseVersion, claim, pool, input.signal);
      if (!source.ok) {
        if (source.blocked) {
          return envelope("denied", source.message, allCitations, allSlices, {
            reasonCode: source.reasonCode,
            output: {
              verdict: "insufficient",
              claim,
              sourcesAttempted,
              judgmentRounds,
            } satisfies ClaimVerifyOutput,
          });
        }
        if (source.reasonCode === "no_match") {
          return envelope("ok", `Insufficient evidence: ${claim}`, allCitations, allSlices, {
            output: {
              verdict: "insufficient",
              claim,
              sourcesAttempted,
              judgmentRounds,
            } satisfies ClaimVerifyOutput,
          });
        }
        return envelope("failed", source.message, allCitations, allSlices, {
          reasonCode: source.reasonCode,
          output: {
            verdict: "insufficient",
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }

      const idx = pool.indexOf(source.source);
      if (idx >= 0) pool.splice(idx, 1);
      sourcesAttempted.push(source.source);
      sourceAttempts += 1;

      let retrieved: { citations: ToolCitation[]; slices: SourceSliceRef[]; failed?: boolean; denied?: boolean; message?: string };
      try {
        retrieved = await this.retrieve(source.source, claim, input.signal);
      } catch (err) {
        const message = err instanceof Error ? err.message : "source_unavailable";
        return envelope("failed", `Source outage · ${source.source}: ${message}`, allCitations, allSlices, {
          reasonCode: "network",
          output: {
            verdict: "insufficient",
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }

      if (retrieved.denied) {
        return envelope("denied", retrieved.message ?? "Source scope revoked.", allCitations, allSlices, {
          reasonCode: "not_authorized",
          output: {
            verdict: "insufficient",
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }
      if (retrieved.failed) {
        return envelope("failed", retrieved.message ?? "Source failed.", allCitations, allSlices, {
          reasonCode: "network",
          output: {
            verdict: "insufficient",
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }

      allCitations.push(...retrieved.citations);
      allSlices.push(...retrieved.slices);

      if (retrieved.citations.length === 0) {
        continue;
      }

      const support = await this.judgeSupport(
        input.caseId,
        input.caseVersion,
        claim,
        retrieved.citations,
        input.signal,
      );
      judgmentRounds += 1;
      if (!support.ok) {
        if (support.blocked) {
          return envelope("denied", support.message, allCitations, allSlices, {
            reasonCode: support.reasonCode,
            output: {
              verdict: "insufficient",
              claim,
              sourcesAttempted,
              judgmentRounds,
            } satisfies ClaimVerifyOutput,
          });
        }
        return envelope("failed", support.message, allCitations, allSlices, {
          reasonCode: support.reasonCode,
          output: {
            verdict: "insufficient",
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }

      if (support.verdict === "supported" || support.verdict === "contradicted") {
        const label = support.verdict === "supported" ? "Supported" : "Contradicted";
        return envelope("ok", `${label}: ${claim}`, allCitations, allSlices, {
          output: {
            verdict: support.verdict,
            claim,
            sourcesAttempted,
            judgmentRounds,
          } satisfies ClaimVerifyOutput,
        });
      }
    }

    return envelope("ok", `Insufficient evidence: ${claim}`, allCitations, allSlices, {
      output: {
        verdict: "insufficient",
        claim,
        sourcesAttempted,
        judgmentRounds,
      } satisfies ClaimVerifyOutput,
    });
  }

  private async chooseSource(
    caseId: string,
    caseVersion: number,
    claim: string,
    pool: readonly ClaimSourceId[],
    signal: AbortSignal,
  ): Promise<
    | { ok: true; source: ClaimSourceId }
    | { ok: false; blocked?: boolean; message: string; reasonCode: string }
  > {
    if (pool.length === 1) return { ok: true, source: pool[0]! };
    const criteria: Record<string, string> = {};
    for (const id of pool) criteria[id] = id;
    criteria.no_match = "None of the permitted sources";

    const request = {
      questionSetId: CLAIM_SOURCE_CHOICE_SET,
      questionSetVersion: "1",
      model: JEV_MODEL,
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: {
        optionCount: pool.length,
        options: pool,
        claimPreview: claim.slice(0, 160),
        policyVersion: "claim-source@1",
      },
      questions: {
        source: {
          type: "choice" as const,
          instructions: "Select one eligible evidence source for this claim.",
          criteria,
          requireNoMatch: true,
        },
      },
      caseId,
      caseVersion,
    };

    const disclosure = await loadDisclosureGate(
      this.deps.store,
      this.deps.clock.now().toISOString(),
      { kind: "session", id: this.deps.sessionId },
      [{ sourceClass: "claim_excerpt", field: "excerpts", text: claim.slice(0, 400) }],
    );
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
        disclosure,
      },
      request,
      signal,
    );

    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      if (category === "disabled" || category === "not_authorized" || category === "missing_secret" || category === "authentication") {
        return {
          ok: false,
          blocked: true,
          message: `Hosted judgment unavailable · ${category}`,
          reasonCode: category,
        };
      }
      return { ok: false, message: category, reasonCode: category };
    }

    const answer = outcome.response.success.answers.source;
    if (!answer || answer.type !== "choice") {
      return { ok: false, message: "invalid_choice", reasonCode: "invalid_response" };
    }
    const probabilities = { ...answer.probabilities };
    const dist = validateChoiceDistribution({
      probabilities,
      declared: answer.choice,
      allowed: pool,
    });
    if (!dist.ok) return { ok: false, message: dist.reasonCode, reasonCode: "invalid_response" };
    const gate = evaluateChoiceGate({
      probabilities,
      minimum: 0.55,
      marginMinimum: 0.1,
    });
    if (!gate.pass || gate.selected === "no_match" || !pool.includes(gate.selected as ClaimSourceId)) {
      if (gate.selected === "no_match" && gate.pass) {
        return { ok: false, message: "No eligible source matched the claim.", reasonCode: "no_match" };
      }
      // Soft gate failure: try the first remaining source as a deterministic fallback.
      return { ok: true, source: pool[0]! };
    }
    return { ok: true, source: gate.selected as ClaimSourceId };
  }

  private async judgeSupport(
    caseId: string,
    caseVersion: number,
    claim: string,
    citations: readonly ToolCitation[],
    signal: AbortSignal,
  ): Promise<
    | { ok: true; verdict: ClaimVerdict }
    | { ok: false; blocked?: boolean; message: string; reasonCode: string }
  > {
    const criteria: Record<string, string> = {
      supported: "Evidence supports the claim",
      contradicted: "Evidence contradicts the claim",
      insufficient: "Evidence is insufficient to decide",
    };
    const excerpts = citations.map((c) => c.snippet ?? c.title).slice(0, 5);
    const request = {
      questionSetId: CLAIM_SUPPORT_SET,
      questionSetVersion: "1",
      model: JEV_MODEL,
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: {
        claimPreview: claim.slice(0, 160),
        citationCount: citations.length,
        excerpts,
        policyVersion: "claim-support@1",
      },
      questions: {
        support: {
          type: "choice" as const,
          instructions: "Classify whether the retrieved evidence supports, contradicts, or is insufficient for the claim.",
          criteria,
          requireNoMatch: false,
        },
      },
      caseId,
      caseVersion,
      sourceObjectRefs: citations.map((c) => ({
        artifactId: c.sourceSlice.artifactId,
        sha256: c.sourceSlice.sha256,
      })),
    };

    const disclosure = await loadDisclosureGate(
      this.deps.store,
      this.deps.clock.now().toISOString(),
      { kind: "session", id: this.deps.sessionId },
      [
        { sourceClass: "claim_excerpt", field: "excerpts", text: claim.slice(0, 400) },
        ...excerpts.map((item) => ({
          sourceClass: "claim_excerpt" as const,
          field: "excerpts" as const,
          text: item.slice(0, 240),
        })),
      ],
    );
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
        disclosure,
      },
      request,
      signal,
    );

    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      if (category === "disabled" || category === "not_authorized" || category === "missing_secret" || category === "authentication") {
        return {
          ok: false,
          blocked: true,
          message: `Hosted judgment unavailable · ${category}`,
          reasonCode: category,
        };
      }
      return { ok: false, message: category, reasonCode: category };
    }

    const answer = outcome.response.success.answers.support;
    if (!answer || answer.type !== "choice") {
      return { ok: true, verdict: "insufficient" };
    }
    const probabilities = { ...answer.probabilities };
    const dist = validateChoiceDistribution({
      probabilities,
      declared: answer.choice,
      allowed: ["supported", "contradicted", "insufficient"],
    });
    if (!dist.ok) return { ok: true, verdict: "insufficient" };
    const gate = evaluateChoiceGate({
      probabilities,
      minimum: 0.55,
      marginMinimum: 0.1,
    });
    if (!gate.pass) return { ok: true, verdict: "insufficient" };
    if (gate.selected === "supported" || gate.selected === "contradicted" || gate.selected === "insufficient") {
      return { ok: true, verdict: gate.selected };
    }
    return { ok: true, verdict: "insufficient" };
  }

  private async retrieve(
    source: ClaimSourceId,
    claim: string,
    signal: AbortSignal,
  ): Promise<{
    citations: ToolCitation[];
    slices: SourceSliceRef[];
    failed?: boolean;
    denied?: boolean;
    message?: string;
  }> {
    if (source === "local_memory") {
      const query = claim.toLowerCase();
      const memories = await this.deps.learning.listMemories();
      const hits = memories.filter((memory) => {
        const hay = `${memory.kind} ${memory.key} ${Object.values(memory.value).join(" ")}`.toLowerCase();
        return hay.includes(query) || query.split(/\s+/).some((token) => token.length > 3 && hay.includes(token));
      });
      const citations: ToolCitation[] = [];
      const slices: SourceSliceRef[] = [];
      for (const hit of hits.slice(0, 5)) {
        const summary =
          hit.kind === "glossary"
            ? `${hit.key}: ${hit.value.expansion ?? ""}`
            : hit.kind === "note"
              ? String(hit.value.text ?? hit.key)
              : `${hit.value.displayName ?? hit.key}`;
        const bytes = encodeText(summary);
        const ref = await this.deps.artifacts.put(bytes, localOnlyPolicy());
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
      return { citations, slices };
    }

    if (source === "public_search") {
      if (!this.deps.publicSearch) {
        return { citations: [], slices: [], denied: true, message: "Public search is not configured." };
      }
      const hits = await this.deps.publicSearch.search(claim, signal);
      const citations: ToolCitation[] = [];
      const slices: SourceSliceRef[] = [];
      for (const hit of hits.slice(0, 5)) {
        const body = `${hit.title}\n${hit.url}\n${hit.snippet}`;
        const bytes = encodeText(body);
        const ref = await this.deps.artifacts.put(bytes, publicPolicy());
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
      return { citations, slices };
    }

    if (!this.deps.github) {
      return { citations: [], slices: [], denied: true, message: "GitHub read is not configured." };
    }
    const hits = await this.deps.github.search(claim, signal);
    const citations: ToolCitation[] = [];
    const slices: SourceSliceRef[] = [];
    for (const hit of hits.slice(0, 5)) {
      const body = `${hit.title}\n${hit.url}\n${hit.snippet}`;
      const bytes = encodeText(body);
      const ref = await this.deps.artifacts.put(bytes, localOnlyPolicy());
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
    return { citations, slices };
  }
}

function envelope(
  status: ToolResultEnvelope["status"],
  summary: string,
  citations: readonly ToolCitation[],
  sourceSlices: readonly SourceSliceRef[],
  extra: Partial<ToolResultEnvelope> = {},
): ToolResultEnvelope {
  return {
    toolId: "claim.verify@1",
    status,
    summary,
    citations,
    sourceSlices,
    output: extra.output ?? {},
    ...(extra.reasonCode ? { reasonCode: extra.reasonCode } : {}),
  };
}
