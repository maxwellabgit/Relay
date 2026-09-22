import type { ToolResultEnvelope, ToolResultStatus } from "@relay/contracts";
import type { ToolBudgets } from "@relay/contracts";
import type { WorkDisposition } from "../judgments/JudgmentService.js";
import type { ArtifactStorePort, JudgmentPort, TextModelPort } from "@relay/contracts";
import { runJudgmentLifecycle } from "../judgment-lifecycle.js";
import { evaluateChoiceGate, validateChoiceDistribution } from "../policies.js";
import { PRIORITY_DIRECT, type WorkItem } from "../queue.js";
import type { Clock, IdFactory, Scheduler } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import { feedItemId, type EngineTrace } from "../engine-helpers.js";
import { knownReason, structuralToolId } from "../runtime-events.js";
import type { OutcomeRecorder } from "../outcomes/OutcomeRecorder.js";
import type { AuthorityState } from "../operations/AuthorityState.js";
import {
  DEFAULT_TOOL_BUDGETS,
  filterEligibleTools,
  hashCanonicalArgs,
  remainingBudgets,
  validateToolArgs,
  type ToolRegistry,
} from "./ToolRegistry.js";
import { TOOL_CLAIM_VERIFY, TOOL_GITHUB_SEARCH, TOOL_MEMORY_SEARCH, TOOL_PUBLIC_SEARCH, TOOL_RESPOND } from "./builtins.js";
import { ClaimVerifier, type ClaimEligibleSources } from "./ClaimVerifier.js";
import type { GitHubReadPort, PublicSearchPort } from "@relay/contracts";

export type ToolBrokerDeps = {
  readonly registry: ToolRegistry;
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly model: TextModelPort;
  readonly judgments: JudgmentPort;
  readonly authority: AuthorityState;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly scheduler: Scheduler;
  readonly outcomes: OutcomeRecorder;
  readonly trace: EngineTrace;
  readonly getAbortSignal: () => AbortSignal;
  readonly getActiveCaseId: () => string | null;
  readonly emitSnapshot: () => Promise<void>;
  readonly mode?: "live" | "recorded" | "replay";
  readonly publicSearch?: PublicSearchPort;
  readonly github?: GitHubReadPort;
};

type BudgetUsage = {
  toolSteps: number;
  judgmentRounds: number;
  sourceAttempts: number;
};

/**
 * Eligibility → route (deterministic or Jev Choice) → args → validate → execute.
 * Control options and unapproved tools never appear in Choice criteria.
 */
export class ToolBroker {
  private readonly claimVerifier: ClaimVerifier;

  constructor(private readonly deps: ToolBrokerDeps) {
    this.claimVerifier = new ClaimVerifier({
      store: deps.store,
      artifacts: deps.artifacts,
      judgments: deps.judgments,
      learning: deps.store.learning,
      clock: deps.clock,
      ids: deps.ids,
      ...(deps.publicSearch ? { publicSearch: deps.publicSearch } : {}),
      ...(deps.github ? { github: deps.github } : {}),
      ...(deps.mode ? { mode: deps.mode } : {}),
    });
  }

  async onToolRoute(item: WorkItem): Promise<WorkDisposition> {
    const caseId = String(item.payload.caseId ?? "");
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.status === "completed" || current.status === "blocked" || current.status === "failed") {
      return { kind: "complete" };
    }
    const text = String(item.payload.text ?? "");
    const usage = readUsage(item.payload);
    const remaining = remainingBudgets({
      maxToolSteps: usage.toolSteps,
      maxJudgmentRounds: usage.judgmentRounds,
      maxSourceAttempts: usage.sourceAttempts,
    });
    if (remaining.maxToolSteps <= 0) {
      await this.failBudget(caseId, current.version, "max_tool_steps");
      return { kind: "complete" };
    }

    const eligible = await this.eligibleDefinitions(remaining);
    const preferToolId = String(item.payload.preferToolId ?? "");
    const claimWanted =
      preferToolId === TOOL_CLAIM_VERIFY ||
      /\b(verify|is it true|check (this |the )?claim|how many .+ (sites|operate)|factual)\b/i.test(text);
    const toolOptions = eligible
      .map((tool) => tool.id)
      .filter((id) => id !== TOOL_RESPOND)
      .filter((id) => id !== TOOL_CLAIM_VERIFY || claimWanted);
    const uniqueOptions = unique([...toolOptions, "respond", "no_match"]);

    if (preferToolId && uniqueOptions.includes(preferToolId)) {
      await this.deps.outcomes.putReceipt({
        caseId,
        gateId: "tool.route",
        policyVersion: "tool-route@1",
        questionType: "deterministic",
        provider: "not_applicable",
        probabilities: {},
        thresholds: {},
        selectedOption: preferToolId,
        selectedOptionId: preferToolId,
        optionLabels: Object.fromEntries(uniqueOptions.map((id) => [id, id])),
        result: "pass",
        reasonCode: "policy_pass",
        latencyMs: null,
      });
      return this.enqueueExecute(item, caseId, current.version, preferToolId, text, usage, "prefer");
    }

    const deterministic = pickDeterministic(text, uniqueOptions);
    if (deterministic) {
      await this.deps.outcomes.putReceipt({
        caseId,
        gateId: "tool.route",
        policyVersion: "tool-route@1",
        questionType: "deterministic",
        provider: "not_applicable",
        probabilities: {},
        thresholds: {},
        selectedOption: deterministic,
        selectedOptionId: deterministic,
        optionLabels: Object.fromEntries(uniqueOptions.map((id) => [id, id])),
        result: "pass",
        reasonCode: "policy_pass",
        latencyMs: null,
      });
      return this.enqueueExecute(item, caseId, current.version, deterministic, text, usage, "deterministic");
    }

    if (remaining.maxJudgmentRounds <= 0) {
      await this.failBudget(caseId, current.version, "max_judgment_rounds");
      return { kind: "complete" };
    }

    const selected = await this.chooseRoute(caseId, current.version, text, uniqueOptions, item);
    if (!selected.ok) {
      if (selected.retry) return selected.retry;
      await this.deps.outcomes.finishCase(caseId, current.version, selected.status, this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    const nextUsage = { ...usage, judgmentRounds: usage.judgmentRounds + 1 };
    if (selected.choice === "no_match" || selected.choice === "clarify" || selected.choice === "wait") {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "task"),
        kind: "task",
        summary:
          selected.choice === "clarify"
            ? "Need one clarification before continuing."
            : selected.choice === "wait"
              ? "Waiting before continuing."
              : "No eligible tool matched this Ask.",
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    return this.enqueueExecute(item, caseId, current.version, selected.choice, text, nextUsage, "jev");
  }

  async onToolExecute(item: WorkItem): Promise<WorkDisposition> {
    const caseId = String(item.payload.caseId ?? "");
    const toolId = String(item.payload.toolId ?? "");
    const text = String(item.payload.text ?? "");
    const usage = readUsage(item.payload);
    const current = await this.deps.store.getCase(caseId);
    if (!current || current.status === "completed" || current.status === "blocked" || current.status === "failed") {
      return { kind: "complete" };
    }

    const remaining = remainingBudgets({
      maxToolSteps: usage.toolSteps,
      maxJudgmentRounds: usage.judgmentRounds,
      maxSourceAttempts: usage.sourceAttempts,
    });
    if (remaining.maxToolSteps <= 0) {
      await this.failBudget(caseId, current.version, "max_tool_steps");
      return { kind: "complete" };
    }

    const eligible = await this.eligibleDefinitions(remaining);
    const allowedIds = new Set(eligible.map((tool) => tool.id));
    if (allowedIds.has(TOOL_RESPOND)) allowedIds.add("respond");
    if (!allowedIds.has(toolId) && toolId !== TOOL_RESPOND) {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "wait"),
        kind: "wait",
        summary: `Tool not eligible: ${toolId}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "blocked", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    const resolvedId = toolId === "respond" ? TOOL_RESPOND : toolId;
    const tool = this.deps.registry.get(resolvedId);
    if (!tool) {
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
      return { kind: "dead", reasonCode: "invalid_gate" };
    }

    const drafted = await this.draftArgs(resolvedId, text);
    const validated = validateToolArgs(tool.definition.inputSchema, drafted);
    if (!validated.ok) {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "wait"),
        kind: "wait",
        summary: `Invalid tool arguments · ${validated.error}`,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    if (resolvedId === TOOL_PUBLIC_SEARCH && remaining.maxSourceAttempts <= 0) {
      await this.failBudget(caseId, current.version, "max_source_attempts");
      return { kind: "complete" };
    }

    const started = Date.now();
    if (resolvedId === TOOL_RESPOND) {
      await this.deps.trace.emit({
        type: "model.requested",
        stage: "model.request",
        status: "waiting",
        caseId,
        reasonCode: "direct_answer",
      });
    }
    const result =
      resolvedId === TOOL_CLAIM_VERIFY
        ? await this.claimVerifier.verify({
            claim: String(validated.value.claim ?? text),
            caseId,
            caseVersion: current.version,
            eligible: await this.claimEligibleSources(),
            signal: this.deps.getAbortSignal(),
          })
        : await tool.execute(validated.value, this.deps.getAbortSignal());
    const durationMs = Date.now() - started;
    if (resolvedId === TOOL_RESPOND) {
      await this.deps.trace.emit({
        type: result.status === "ok" ? "model.completed" : "model.failed",
        stage: "model.response",
        status: result.status === "ok" ? "completed" : "failed",
        caseId,
        reasonCode: result.status === "ok" ? "completed" : result.reasonCode ?? "model_unavailable",
        durationMs,
      });
    }
    await this.persistResult(
      caseId,
      current.version,
      result,
      durationMs,
      await hashCanonicalArgs(validated.value),
    );

    const nextUsage: BudgetUsage = {
      toolSteps: usage.toolSteps + 1,
      judgmentRounds: usage.judgmentRounds,
      sourceAttempts:
        usage.sourceAttempts +
        (resolvedId === TOOL_PUBLIC_SEARCH ||
        resolvedId === TOOL_MEMORY_SEARCH ||
        resolvedId === TOOL_GITHUB_SEARCH ||
        resolvedId === TOOL_CLAIM_VERIFY
          ? 1
          : 0),
    };

      if (result.status === "ok" || result.status === "empty") {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "answer"),
        kind: "answer",
        summary: formatAnswer(result),
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.trace.emit({
        type: "answer.committed",
        stage: "episode.complete",
        status: "completed",
        caseId,
        reasonCode: result.status === "ok" ? "completed" : "no_candidates",
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "completed", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    if (result.status === "denied" || result.status === "needs_approval") {
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, "wait"),
        kind: "wait",
        summary: result.summary,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(caseId, current.version, "blocked", this.deps.getActiveCaseId());
      return { kind: "complete" };
    }

    // Claim verify failures never re-route to another tool — finish blocked or failed.
    if (resolvedId === TOOL_CLAIM_VERIFY && result.status === "failed") {
      const recoverable =
        result.reasonCode === "network" ||
        result.reasonCode === "not_authorized" ||
        result.reasonCode === "authentication" ||
        result.reasonCode === "missing_secret" ||
        result.reasonCode === "timeout" ||
        result.reasonCode === "rate_limited" ||
        result.reasonCode === "overloaded";
      await this.deps.outcomes.publishFeedItem({
        itemId: feedItemId(caseId, recoverable ? "wait" : "answer"),
        kind: recoverable ? "wait" : "answer",
        summary: result.summary,
        createdAt: this.deps.clock.now().toISOString(),
        caseId,
      });
      await this.deps.outcomes.finishCase(
        caseId,
        current.version,
        recoverable ? "blocked" : "failed",
        this.deps.getActiveCaseId(),
      );
      return { kind: "complete" };
    }

    // Failed tool: one more route only when budget remains.
    if (remainingBudgets({
      maxToolSteps: nextUsage.toolSteps,
      maxJudgmentRounds: nextUsage.judgmentRounds,
      maxSourceAttempts: nextUsage.sourceAttempts,
    }).maxToolSteps > 0) {
      await this.deps.scheduler.enqueue(
        "tool.route",
        {
          caseId,
          caseVersion: current.version,
          text,
          toolSteps: nextUsage.toolSteps,
          judgmentRounds: nextUsage.judgmentRounds,
          sourceAttempts: nextUsage.sourceAttempts,
        },
        PRIORITY_DIRECT,
        this.deps.ids,
        0,
        { parentWorkId: item.workId, correlationId: caseId },
      );
      return { kind: "complete" };
    }

    await this.deps.outcomes.publishFeedItem({
      itemId: feedItemId(caseId, "answer"),
      kind: "answer",
      summary: result.summary || "No local result for this Ask.",
      createdAt: this.deps.clock.now().toISOString(),
      caseId,
    });
    await this.deps.outcomes.finishCase(caseId, current.version, "failed", this.deps.getActiveCaseId());
    return { kind: "complete" };
  }

  private async eligibleDefinitions(remaining: ToolBudgets) {
    const projection = await this.deps.authority.project();
    const connected = new Set(
      projection.connections.filter((c) => c.connected).map((c) => c.connector.id),
    );
    const scopes = new Set<string>();
    for (const connection of projection.connections) {
      for (const scope of connection.grantedOAuthScopes) scopes.add(scope);
      for (const scope of connection.readScopes) scopes.add(scope);
    }
    const publicSearchConnected = connected.has("public-search");
    const hasPublicDisclosure = projection.disclosures.some((grant) => {
      if (grant.disclosure !== "public") return false;
      const connection = projection.connections.find((row) => row.connectionId === grant.connectionId);
      return connection?.connector.id === "public-search" && connection.connected;
    });
    const hostedEnabled = await this.deps.store.getHostedProcessingEnabled();
    // Public-search (and any Jev Choice among tools) requires hosted processing + connected connector + disclosure.
    const publicSearchAvailable =
      hostedEnabled &&
      this.deps.registry.get(TOOL_PUBLIC_SEARCH) != null &&
      publicSearchConnected &&
      hasPublicDisclosure;
    const githubAvailable =
      this.deps.registry.get(TOOL_GITHUB_SEARCH) != null &&
      this.deps.github != null &&
      connected.has("github");
    // Claim verify always has local memory; hosted needed only when choosing among multiple sources via Jev.
    const claimVerifyAvailable = this.deps.registry.get(TOOL_CLAIM_VERIFY) != null;
    return filterEligibleTools(this.deps.registry.definitions(), {
      caseOrigin: "direct",
      remaining,
      connectedConnectorIds: connected,
      grantedScopes: scopes,
      hasPublicDisclosure,
      publicSearchAvailable,
      githubAvailable,
      claimVerifyAvailable,
    });
  }

  private async claimEligibleSources(): Promise<ClaimEligibleSources> {
    const projection = await this.deps.authority.project();
    const connected = new Set(
      projection.connections.filter((c) => c.connected).map((c) => c.connector.id),
    );
    const hasPublicDisclosure = projection.disclosures.some((grant) => {
      if (grant.disclosure !== "public") return false;
      const connection = projection.connections.find((row) => row.connectionId === grant.connectionId);
      return connection?.connector.id === "public-search" && connection.connected;
    });
    const hostedEnabled = await this.deps.store.getHostedProcessingEnabled();
    return {
      localMemory: true,
      publicSearch:
        hostedEnabled &&
        this.deps.publicSearch != null &&
        connected.has("public-search") &&
        hasPublicDisclosure,
      github: this.deps.github != null && connected.has("github"),
    };
  }

  private async chooseRoute(
    caseId: string,
    caseVersion: number,
    text: string,
    optionIds: readonly string[],
    item: WorkItem,
  ): Promise<
    | { ok: true; choice: string }
    | { ok: false; status: "failed" | "blocked"; retry?: WorkDisposition }
  > {
    const criteria: Record<string, string> = {};
    for (const id of optionIds) criteria[id] = id;
    if (!("no_match" in criteria)) criteria.no_match = "None of the permitted choices";

    const request = {
      questionSetId: "judgment.tool-route",
      questionSetVersion: "1",
      model: "jev-1.13.0",
      provider: this.deps.mode === "recorded" ? "recorded" : "typesafe",
      state: {
        optionCount: optionIds.length,
        options: optionIds,
        askPreview: text.slice(0, 160),
        policyVersion: "tool-route@1",
      },
      questions: {
        route: {
          type: "choice" as const,
          instructions: "Select one eligible tool or control option.",
          criteria,
          requireNoMatch: true,
        },
      },
      caseId,
      caseVersion,
    };

    const started = Date.now();
    const outcome = await runJudgmentLifecycle(
      {
        store: this.deps.store,
        artifacts: this.deps.artifacts,
        judgments: this.deps.judgments,
        clock: this.deps.clock,
        ids: this.deps.ids,
        isHostedProcessingAllowed: () => this.deps.store.getHostedProcessingEnabled(),
      },
      request,
      this.deps.getAbortSignal(),
    );
    const durationMs = Date.now() - started;
    if (!outcome.response.ok) {
      const category = outcome.response.failure.category;
      const reasonCode =
        outcome.response.failure.message === "hosted_processing_disabled"
          ? knownReason("hosted_processing_disabled")
          : knownReason(category);
      const blocked =
        category === "missing_secret" ||
        category === "authentication" ||
        category === "disabled" ||
        category === "not_authorized";
      await this.deps.outcomes.putReceipt({
        caseId,
        gateId: "tool.route",
        policyVersion: "tool-route@1",
        questionType: "choice",
        provider: request.provider,
        probabilities: {},
        thresholds: { choiceProbabilityMinimum: 0.65, choiceMarginMinimum: 0.15 },
        selectedOption: null,
        result: blocked ? "blocked" : "fail",
        reasonCode,
        latencyMs: durationMs,
        judgmentId: outcome.record.judgmentId,
      });
      await this.deps.trace.emit({
        type: "judgment.failed",
        stage: "judgment.response",
        status: "failed",
        caseId,
        judgmentId: outcome.record.judgmentId,
        reasonCode,
        durationMs,
      });
      void item;
      return { ok: false, status: blocked ? "blocked" : "failed" };
    }

    const answer = outcome.response.success.answers.route;
    if (!answer || answer.type !== "choice") return { ok: false, status: "failed" };

    const probabilities = { ...answer.probabilities };
    const distribution = validateChoiceDistribution({
      probabilities,
      declared: answer.choice,
      allowed: optionIds,
    });
    if (!distribution.ok) return { ok: false, status: "failed" };

    const gate = evaluateChoiceGate({
      probabilities,
      minimum: 0.65,
      marginMinimum: 0.15,
    });
    const selected = gate.selected ?? answer.choice;
    const control = selected === "no_match" || selected === "clarify" || selected === "wait";
    const pass = control
      ? gate.pass && selected === answer.choice
      : gate.pass && gate.selected === answer.choice && gate.selected !== "no_match";
    await this.deps.outcomes.putReceipt({
      caseId,
      gateId: "tool.route",
      policyVersion: "tool-route@1",
      questionType: "choice",
      provider: request.provider,
      probabilities,
      thresholds: { choiceProbabilityMinimum: 0.65, choiceMarginMinimum: 0.15 },
      selectedOption: selected,
      selectedOptionId: selected,
      optionLabels: Object.fromEntries(optionIds.map((id) => [id, id])),
      judgmentId: outcome.record.judgmentId,
      result: pass ? "pass" : "fail",
      reasonCode: pass ? (control ? "no_match" : "policy_pass") : gate.reasonCode,
      latencyMs: durationMs,
    });
    await this.deps.trace.emit({
      type: "judgment.completed",
      stage: "judgment.response",
      status: "completed",
      caseId,
      judgmentId: outcome.record.judgmentId,
      reasonCode: pass ? (control ? "no_match" : "policy_pass") : gate.reasonCode,
      durationMs,
    });
    void item;
    if (!pass || !selected) return { ok: false, status: "failed" };
    return { ok: true, choice: selected };
  }

  private async draftArgs(toolId: string, text: string): Promise<Record<string, unknown>> {
    if (toolId === TOOL_RESPOND) return { text };
    if (toolId === TOOL_CLAIM_VERIFY) {
      const stripped = text.replace(/^(verify|check|is it true that)\s+/i, "").trim();
      return { claim: stripped || text.trim() };
    }
    if (toolId === TOOL_MEMORY_SEARCH) {
      const token = /\b([A-Z0-9]{2,12})\b/.exec(text)?.[1];
      if (token) return { query: token };
      const stripped = text.replace(/search\s+(my\s+)?memory\s+for\s+/i, "").trim();
      if (stripped) return { query: stripped };
    }
    // Local model may draft query language only; code still validates schema.
    const drafted = await this.deps.model.generate(
      {
        taskKind: "tool_args",
        prompt: `Extract a short search query from this Ask. Reply with the query only.\n\nAsk: ${text}`,
        maxTokens: 40,
        temperature: 0,
      },
      this.deps.getAbortSignal(),
    );
    if (drafted.ok && drafted.text.trim()) {
      return { query: drafted.text.trim().replace(/^["']|["']$/g, "") };
    }
    return { query: text.trim() };
  }

  private async enqueueExecute(
    item: WorkItem,
    caseId: string,
    caseVersion: number,
    toolId: string,
    text: string,
    usage: BudgetUsage,
    route: string,
  ): Promise<WorkDisposition> {
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.updateCase(caseId, caseVersion, {
      phase: "execute",
      status: "active",
      waitKind: null,
      at,
    });
    await this.deps.store.appendCaseEvent(caseId, caseVersion, "tool.routed", at, {
      toolId,
      route,
      toolSteps: usage.toolSteps,
      judgmentRounds: usage.judgmentRounds,
      sourceAttempts: usage.sourceAttempts,
    });
    const routedId = structuralToolId(toolId);
    await this.deps.trace.emit({
      type: "tool.routed",
      stage: "tool.execute",
      status: "started",
      caseId,
      reasonCode: "start",
      ...(routedId ? { toolId: routedId } : {}),
    });
    await this.deps.scheduler.enqueue(
      "tool.execute",
      {
        caseId,
        caseVersion,
        toolId,
        text,
        toolSteps: usage.toolSteps,
        judgmentRounds: usage.judgmentRounds,
        sourceAttempts: usage.sourceAttempts,
      },
      PRIORITY_DIRECT,
      this.deps.ids,
      0,
      { parentWorkId: item.workId, correlationId: caseId },
    );
    await this.deps.trace.emit({
      type: "policy.evaluated",
      stage: "policy.evaluate",
      status: "completed",
      caseId,
      reasonCode: "policy_pass",
    });
    return { kind: "complete" };
  }

  private async persistResult(
    caseId: string,
    caseVersion: number,
    result: ToolResultEnvelope,
    durationMs: number,
    canonicalHash: string,
  ): Promise<void> {
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.appendCaseEvent(caseId, caseVersion, "tool.completed", at, {
      toolId: result.toolId,
      status: result.status,
      citationCount: result.citations.length,
      sourceSliceCount: result.sourceSlices.length,
      canonicalHash,
      durationMs,
      citations: result.citations.map((c) => ({
        title: c.title,
        url: c.url ?? null,
        artifactId: c.sourceSlice.artifactId,
        sha256: c.sourceSlice.sha256,
        start: c.sourceSlice.start,
        end: c.sourceSlice.end,
      })),
    });
    const completedId = structuralToolId(result.toolId);
    await this.deps.trace.emit({
      type: "tool.completed",
      stage: "tool.execute",
      status: toolTraceStatus(result.status),
      caseId,
      reasonCode: toolTraceReason(result.status, result.reasonCode),
      durationMs,
      ...(completedId ? { toolId: completedId } : {}),
    });
    await this.deps.trace.emit({
      type: "outcome.recorded",
      stage: "episode.complete",
      status: "completed",
      caseId,
      reasonCode: result.status === "ok" ? "completed" : result.reasonCode ?? "fail",
      durationMs,
    });
  }

  private async failBudget(caseId: string, caseVersion: number, reasonCode: string): Promise<void> {
    await this.deps.outcomes.publishFeedItem({
      itemId: feedItemId(caseId, "wait"),
      kind: "wait",
      summary: `Budget exhausted · ${reasonCode}`,
      createdAt: this.deps.clock.now().toISOString(),
      caseId,
    });
    await this.deps.outcomes.finishCase(caseId, caseVersion, "failed", this.deps.getActiveCaseId());
  }
}

function toolTraceStatus(status: ToolResultStatus): "completed" | "failed" | "waiting" {
  if (status === "needs_approval") return "waiting";
  if (status === "ok" || status === "empty") return "completed";
  return "failed";
}

function toolTraceReason(status: ToolResultStatus, reasonCode?: string): string {
  if (status === "ok") return "completed";
  if (status === "empty") return "no_match";
  if (status === "denied") return "not_authorized";
  if (status === "needs_approval") return "confirmation_required";
  return knownReason(reasonCode ?? "fail");
}

function readUsage(payload: Record<string, unknown>): BudgetUsage {
  return {
    toolSteps: Number(payload.toolSteps ?? 0),
    judgmentRounds: Number(payload.judgmentRounds ?? 0),
    sourceAttempts: Number(payload.sourceAttempts ?? 0),
  };
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values)];
}

function pickDeterministic(text: string, options: readonly string[]): string | null {
  const lower = text.toLowerCase();
  const memoryHint =
    /\b(remember|birthday|glossary|what did i save|my notes|search (my )?memory)\b/i.test(lower);
  const searchHint = /\b(search online|look up|public search|web search)\b/i.test(lower);
  const verifyHint =
    /\b(verify|is it true|check (this |the )?claim|how many .+ (sites|operate)|factual)\b/i.test(lower);
  const githubHint = /\b(github|pull request|\bpr\b|issue #)\b/i.test(lower);
  if (githubHint && options.includes(TOOL_GITHUB_SEARCH)) return TOOL_GITHUB_SEARCH;
  if (verifyHint && options.includes(TOOL_CLAIM_VERIFY)) return TOOL_CLAIM_VERIFY;
  if (memoryHint && options.includes(TOOL_MEMORY_SEARCH)) return TOOL_MEMORY_SEARCH;
  if (searchHint && options.includes(TOOL_PUBLIC_SEARCH)) return TOOL_PUBLIC_SEARCH;
  // When public-search or github is eligible, Jev chooses among respond / memory / tools.
  if (options.includes(TOOL_PUBLIC_SEARCH) || options.includes(TOOL_GITHUB_SEARCH)) return null;
  if (options.includes("respond")) return "respond";
  return null;
}

function formatAnswer(result: ToolResultEnvelope): string {
  if (result.citations.length === 0) return result.summary;
  const cites = result.citations
    .map((c, index) => {
      const loc = c.url ?? `artifact:${c.sourceSlice.artifactId}`;
      return `[${index + 1}] ${c.title} — ${loc}`;
    })
    .join("\n");
  return `${result.summary}\n\nSources:\n${cites}`;
}

export { DEFAULT_TOOL_BUDGETS };
