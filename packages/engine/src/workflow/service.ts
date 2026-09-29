import type {
  ArtifactStorePort,
  EpisodeClassification,
  RawActivitySignal,
  StoredObservation,
  WorkflowCommand,
  WorkflowHostStatus,
  WorkflowProposal,
  WorkflowReflexRecord,
  WorkflowSettings,
  WorkflowView,
  WorkEpisodeRecord,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { evaluateChoiceGate } from "../policies.js";
import type { FoundationStore } from "../cases/foundation-store.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { TraceEmitInput } from "../engine-helpers.js";
import {
  classifyObservations,
  commonSteps,
  EPISODE_GAP_MS,
  evidenceFor,
  PATTERN_MINIMUM,
  related,
} from "./classify.js";
import { phraseChange, tailorDocuments } from "./drafts.js";
import { acceptSignal } from "./policy.js";

const SETTINGS = "workflow_settings";
const OBSERVATION = "workflow_observation";
const EPISODE = "workflow_episode";
const PROPOSAL = "workflow_proposal";
const REFLEX = "workflow_reflex";
const HOST = "workflow_host";
const PREVIEW = "workflow_preview";
const JUDGMENT = "workflow_judgment";
const CORRECTION = "workflow_correction";
const LOCAL = "local";

const DEFAULT_SETTINGS: WorkflowSettings = {
  setupComplete: false,
  paused: true,
  windowsEnabled: false,
  chromeEnabled: false,
  pageContentEnabled: false,
  retentionDays: 7,
  allowedSites: [],
  allowedFolders: [],
  masterResumePath: null,
  patternSuppressed: false,
};

const DEFAULT_HOST: WorkflowHostStatus = {
  windows: "stopped",
  chrome: "disconnected",
  windowsDetail: "Windows observation is off.",
  chromeDetail: "Chrome is not connected.",
};

const REFLEX_CAPABILITIES = ["resume.read_approved", "draft.write_copy"] as const;
const REFLEX_FORBIDDEN = ["resume.overwrite_master", "application.submit", "email.send", "form.fill", "permission.widen"] as const;

export type WorkflowFiles = {
  readText(path: string): Promise<string>;
  writeDraft(name: string, body: string): Promise<string>;
};

export type WorkflowJudge = (episodeId: string) => Promise<
  | { readonly ok: true; readonly choice: string; readonly probabilities: Readonly<Record<string, number>> }
  | { readonly ok: false; readonly reason: string }
>;

export type WorkflowServiceDeps = {
  readonly records: FoundationStore;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly trace: (input: TraceEmitInput) => Promise<void>;
  readonly files?: WorkflowFiles;
  readonly judge?: WorkflowJudge;
  readonly artifacts?: ArtifactStorePort;
  readonly forget?: (artifactId: string) => Promise<void>;
};

type CorrectionRecord = {
  readonly correctionId: string;
  readonly episodeId: string;
  readonly removed: string;
  readonly added: string;
  readonly at: string;
};

export class WorkflowService {
  private readonly sessionSites = new Set<string>();
  private lastDraft: WorkflowView["lastDraft"] = null;
  private shadow: WorkflowView["shadowPreview"] = null;

  constructor(private readonly deps: WorkflowServiceDeps) {}

  async view(): Promise<WorkflowView> {
    const settings = await this.settings();
    const host = await this.host();
    const episodes = (await this.episodes()).sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt)).slice(0, 40);
    const observations = await this.observations();
    const latest = [...observations].sort((a, b) => Date.parse(b.observedAt) - Date.parse(a.observedAt))[0];
    const currentEpisode = latest ? episodes.find((item) => item.episodeId === latest.episodeId) : undefined;
    const preview = this.shadow ?? (await this.loadPreview());
    const judgment = await this.deps.records.get(JUDGMENT, LOCAL);
    return {
      settings,
      host: settings.paused
        ? { ...host, windows: "paused", chrome: settings.chromeEnabled ? "paused" : host.chrome, windowsDetail: "Observation is paused.", chromeDetail: settings.chromeEnabled ? "Chrome observation is paused." : host.chromeDetail }
        : host,
      sessionSites: [...this.sessionSites],
      current: latest
        ? {
            application: latest.application,
            title: latest.title,
            since: latest.observedAt,
            classification: currentEpisode?.classification ?? null,
            episodeId: latest.episodeId,
            uncertain: currentEpisode?.classification === "uncertain" || currentEpisode?.confidence === "low",
          }
        : null,
      episodes: episodes.map((episode) => ({
        episodeId: episode.episodeId,
        classification: episode.classification,
        outcome: episode.outcome,
        confidence: episode.confidence,
        rationale: episode.rationale,
        startedAt: episode.startedAt,
        endedAt: episode.endedAt,
        caseId: episode.caseId,
        company: episode.company,
        position: episode.position,
        evidence: episode.evidence,
        locked: episode.locked,
        uncertain: episode.classification === "uncertain" || episode.confidence === "low",
      })),
      proposals: await this.proposals(),
      reflex: await this.reflex(),
      shadowPreview: preview,
      lastDraft: this.lastDraft,
      judgment: (judgment?.payload as WorkflowView["judgment"]) ?? null,
    };
  }

  async execute(command: WorkflowCommand): Promise<{ ok: boolean; summary: string; caseId?: string }> {
    switch (command.action) {
      case "configure":
        return this.configure(command);
      case "ingest":
        return this.ingest(command.signals);
      case "correct":
        return this.correct(command);
      case "assign_case":
        return this.assignCase(command.episodeId, command.caseId);
      case "ensure_job_case":
        return this.ensureJobCase();
      case "decide_proposal":
        return this.decideProposal(command.proposalId, command.decision);
      case "preview_draft":
        return this.preview(command.episodeId);
      case "run_draft":
        return this.runDraft(command.episodeId);
      case "activate_reflex":
        return this.setReflexState("active");
      case "pause_reflex":
        return this.setReflexState("paused");
      case "record_outcome":
        return this.recordOutcome(command.episodeId, command.outcome);
      case "record_correction":
        return this.recordCorrection(command.episodeId, command.before, command.after);
      case "decide_improvement":
        return this.decideImprovement(command.proposalId, command.decision, command.preference);
      case "delete_observations":
        return this.deleteObservations();
      case "host_status":
        await this.deps.records.put(HOST, LOCAL, 1, command.host, this.now());
        return { ok: true, summary: "host_status" };
      case "set_master_resume":
        return this.setMaster(command.path);
      case "judge_episode":
        return this.judgeEpisode(command.episodeId);
      default:
        return { ok: false, summary: "unsupported_command" };
    }
  }

  private async configure(command: Extract<WorkflowCommand, { action: "configure" }>): Promise<{ ok: boolean; summary: string }> {
    const current = await this.settings();
    const retention = command.retentionDays ?? current.retentionDays;
    if (retention < 1 || retention > 30) return { ok: false, summary: "malformed_observation" };
    const allowedSites = (command.allowedSites ?? current.allowedSites).map((site) => site.trim().toLowerCase()).filter(Boolean);
    if (command.allowSessionSite) {
      const site = command.allowSessionSite.trim().toLowerCase();
      if (!/^[a-z0-9.-]+$/.test(site)) return { ok: false, summary: "malformed_observation" };
      this.sessionSites.add(site);
    }
    const next: WorkflowSettings = {
      ...current,
      setupComplete: command.setupComplete ?? current.setupComplete,
      paused: command.paused ?? current.paused,
      windowsEnabled: command.windowsEnabled ?? current.windowsEnabled,
      chromeEnabled: command.chromeEnabled ?? current.chromeEnabled,
      pageContentEnabled: command.pageContentEnabled ?? current.pageContentEnabled,
      retentionDays: retention,
      allowedSites,
      allowedFolders: command.allowedFolders ?? current.allowedFolders,
    };
    await this.deps.records.put(SETTINGS, LOCAL, 1, next, this.now());
    await this.trace("policy.evaluated", "policy.evaluate", next.paused ? "observation_paused" : "policy_pass");
    return { ok: true, summary: "workflow_configured" };
  }

  private async ingest(signals: readonly RawActivitySignal[]): Promise<{ ok: boolean; summary: string }> {
    await this.expire();
    const settings = await this.settings();
    const deletedAt = settings.historyDeletedAt ? Date.parse(settings.historyDeletedAt) : 0;
    let kept = 0;
    for (const signal of signals) {
      if (deletedAt && Date.parse(signal.observedAt) <= deletedAt) {
        await this.trace("source.rejected", "source.accept", "history_deleted", "completed");
        continue;
      }
      const decision = acceptSignal(signal, { settings, sessionSites: [...this.sessionSites], now: this.now() });
      if (!decision.ok) {
        await this.trace("source.rejected", "source.accept", decision.reason, "failed");
        continue;
      }
      if (await this.duplicate(decision.observation)) {
        await this.trace("source.rejected", "source.accept", "duplicate_event", "completed");
        continue;
      }
      const observation = await this.seal({
        ...decision.observation,
        observationId: this.deps.ids.next("obs"),
        permissionReceiptId: this.deps.ids.next("permit"),
        episodeId: null,
      });
      await this.place(observation);
      kept += 1;
      await this.trace("source.accepted", "source.accept", "observation_kept");
    }
    await this.proposePattern();
    return { ok: true, summary: kept > 0 ? `ingested_${kept}` : "ingested_0" };
  }

  private async place(observation: StoredObservation): Promise<void> {
    const episodes = await this.episodes();
    const observations = await this.observations();
    for (const episode of episodes) {
      if (episode.closed) continue;
      if (Date.parse(observation.observedAt) - Date.parse(episode.endedAt) > EPISODE_GAP_MS) {
        await this.deps.records.put(EPISODE, episode.episodeId, episode.version + 1, { ...episode, closed: true, version: episode.version + 1 }, this.now());
      }
    }
    const fresh = await this.episodes();
    const match = fresh
      .filter((episode) => !episode.closed)
      .map((episode) => ({ episode, members: observations.filter((item) => item.episodeId === episode.episodeId) }))
      .filter((item) => item.members.length > 0 && related(item.members, observation))
      .sort((a, b) => Date.parse(b.episode.endedAt) - Date.parse(a.episode.endedAt))[0];
    if (!match) {
      const classified = classifyObservations([observation]);
      const episode: WorkEpisodeRecord = {
        episodeId: this.deps.ids.next("episode"),
        version: 1,
        ...classified,
        startedAt: observation.observedAt,
        endedAt: observation.observedAt,
        caseId: null,
        evidence: [evidenceFor(observation)],
        locked: false,
        closed: false,
      };
      await this.deps.records.put(EPISODE, episode.episodeId, 1, episode, this.now());
      await this.deps.records.put(OBSERVATION, observation.observationId, 1, { ...observation, episodeId: episode.episodeId }, this.now());
      await this.trace("episode.recorded", "episode.complete", "episode_classified", "completed", episode.episodeId);
      return;
    }
    const members = [...match.members, observation];
    const classified = classifyObservations(members);
    const next: WorkEpisodeRecord = match.episode.locked
      ? {
          ...match.episode,
          version: match.episode.version + 1,
          endedAt: observation.observedAt,
          evidence: [...match.episode.evidence, evidenceFor(observation)],
        }
      : {
          ...match.episode,
          ...classified,
          version: match.episode.version + 1,
          startedAt: match.episode.startedAt,
          endedAt: observation.observedAt,
          caseId: match.episode.caseId,
          evidence: [...match.episode.evidence, evidenceFor(observation)],
          locked: false,
          closed: false,
        };
    await this.deps.records.put(EPISODE, next.episodeId, next.version, next, this.now());
    await this.deps.records.put(OBSERVATION, observation.observationId, 1, { ...observation, episodeId: next.episodeId }, this.now());
    await this.trace("episode.recorded", "episode.complete", "episode_classified", "completed", next.episodeId);
  }

  private async correct(command: Extract<WorkflowCommand, { action: "correct" }>): Promise<{ ok: boolean; summary: string }> {
    const episode = await this.episode(command.episodeId);
    if (!episode) return { ok: false, summary: "not_running" };
    const next: WorkEpisodeRecord = {
      ...episode,
      version: episode.version + 1,
      classification: command.classification ?? episode.classification,
      startedAt: command.startedAt ?? episode.startedAt,
      endedAt: command.endedAt ?? episode.endedAt,
      company: command.company ?? episode.company,
      position: command.position ?? episode.position,
      locked: true,
    };
    await this.deps.records.put(EPISODE, next.episodeId, next.version, next, this.now());
    await this.trace("episode.recorded", "episode.complete", "episode_classified", "completed", next.episodeId);
    return { ok: true, summary: "episode_corrected" };
  }

  private async assignCase(episodeId: string, caseId: string | null): Promise<{ ok: boolean; summary: string; caseId?: string }> {
    const episode = await this.episode(episodeId);
    if (!episode) return { ok: false, summary: "not_running" };
    if (caseId) {
      const row = await this.deps.records.get("project_case", caseId);
      if (!row) return { ok: false, summary: "case_missing" };
    }
    const next = { ...episode, version: episode.version + 1, caseId, locked: true };
    await this.deps.records.put(EPISODE, episode.episodeId, next.version, next, this.now());
    return { ok: true, summary: "case_assigned", ...(caseId ? { caseId } : {}) };
  }

  private async ensureJobCase(): Promise<{ ok: boolean; summary: string; caseId: string }> {
    const caseId = "case_job_search";
    const existing = await this.deps.records.get("project_case", caseId);
    if (!existing) {
      const at = this.now();
      await this.deps.records.put(
        "project_case",
        caseId,
        1,
        {
          projectCaseId: caseId,
          alias: "Job Search",
          status: "active",
          version: 1,
          intent: "Track job applications, sources, and tailored drafts without inventing experience.",
          entries: [],
          references: [],
          rules: [],
          createdAt: at,
          updatedAt: at,
          mainSha: "",
        },
        at,
      );
    }
    return { ok: true, summary: "job_case_ready", caseId };
  }

  private async proposePattern(): Promise<void> {
    const settings = await this.settings();
    if (settings.patternSuppressed) return;
    if (await this.reflex()) return;
    const existing = (await this.proposals()).find((item) => item.kind === "pattern");
    if (existing && existing.state !== "pending") return;
    const episodes = (await this.episodes()).filter((item) => item.classification === "job_application");
    if (episodes.length < PATTERN_MINIMUM) return;
    const steps = commonSteps(episodes);
    const proposal: WorkflowProposal = {
      proposalId: existing?.proposalId ?? this.deps.ids.next("proposal"),
      kind: "pattern",
      state: "pending",
      title: "Job Application",
      explanation: `You have performed a similar workflow ${episodes.length} times. Common steps: ${steps.join("; ")}. Create a Reflex?`,
      episodeIds: episodes.map((item) => item.episodeId),
      examples: steps,
      updatedAt: this.now(),
    };
    await this.deps.records.put(PROPOSAL, proposal.proposalId, 1, proposal, this.now());
    await this.trace("candidate.updated", "pattern.update", "pattern_proposed");
  }

  private async decideProposal(proposalId: string, decision: "accept" | "reject" | "suppress"): Promise<{ ok: boolean; summary: string }> {
    const proposal = (await this.proposals()).find((item) => item.proposalId === proposalId && item.kind === "pattern");
    if (!proposal || proposal.state !== "pending") return { ok: false, summary: "not_proposed" };
    if (decision === "accept") {
      const reflex = this.shadowReflex("Approved from repeated job-application episodes.");
      await this.deps.records.put(REFLEX, reflex.reflexId, reflex.version, reflex, this.now());
      await this.saveProposal({ ...proposal, state: "accepted", updatedAt: this.now() });
      await this.trace("candidate.approved", "proposal.create", "user_approval", "completed", undefined, reflex.reflexId);
      return { ok: true, summary: "reflex_shadow" };
    }
    const settings = await this.settings();
    if (decision === "suppress") {
      await this.deps.records.put(SETTINGS, LOCAL, 1, { ...settings, patternSuppressed: true }, this.now());
    }
    await this.saveProposal({ ...proposal, state: decision === "suppress" ? "suppressed" : "rejected", updatedAt: this.now() });
    await this.trace("candidate.rejected", "proposal.create", "user_reject");
    return { ok: true, summary: decision === "suppress" ? "pattern_suppressed" : "pattern_rejected" };
  }

  private async preview(episodeId: string): Promise<{ ok: boolean; summary: string }> {
    const built = await this.compose(episodeId, false);
    if (!built.ok) return built;
    this.shadow = { resume: built.resume, coverLetter: built.coverLetter, notClaimed: built.notClaimed };
    if (this.deps.artifacts) {
      const resumeRef = await this.deps.artifacts.put(new TextEncoder().encode(built.resume), localOnlyPolicy());
      const letterRef = await this.deps.artifacts.put(new TextEncoder().encode(built.coverLetter), localOnlyPolicy());
      await this.deps.records.put(PREVIEW, LOCAL, 1, { resumeRef, letterRef }, this.now());
    }
    await this.trace("policy.evaluated", "policy.evaluate", "shadow_preview", "completed", episodeId, "reflex.job-application");
    return { ok: true, summary: "shadow_preview" };
  }

  private async runDraft(episodeId: string): Promise<{ ok: boolean; summary: string }> {
    const reflex = await this.reflex();
    if (!reflex || reflex.state !== "active") return { ok: false, summary: "not_active" };
    if (!this.deps.files) return { ok: false, summary: "draft_writer_unavailable" };
    const built = await this.compose(episodeId, true);
    if (!built.ok) return built;
    const stamp = built.company.replace(/[^a-z0-9]+/gi, "-").replace(/^-|-$/g, "").slice(0, 40) || "job";
    const resumePath = await this.deps.files.writeDraft(`${stamp}-resume.md`, built.resume);
    const coverLetterPath = await this.deps.files.writeDraft(`${stamp}-cover-letter.md`, built.coverLetter);
    const master = (await this.settings()).masterResumePath;
    if (master && (resumePath === master || coverLetterPath === master)) return { ok: false, summary: "sensitive_blocked" };
    const receiptId = this.deps.ids.next("receipt");
    const next: WorkflowReflexRecord = {
      ...reflex,
      runs: [...reflex.runs, { receiptId, episodeId, at: this.now(), resumePath, coverLetterPath }],
    };
    await this.deps.records.put(REFLEX, next.reflexId, next.version, next, this.now());
    this.lastDraft = { resumePath, coverLetterPath, receiptId };
    await this.trace("tool.completed", "tool.execute", "draft_written", "completed", episodeId, next.reflexId);
    return { ok: true, summary: "draft_written" };
  }

  private async compose(
    episodeId: string,
    requireActive: boolean,
  ): Promise<{ ok: true; resume: string; coverLetter: string; notClaimed: readonly string[]; company: string } | { ok: false; summary: string }> {
    const reflex = await this.reflex();
    if (!reflex) return { ok: false, summary: "not_active" };
    if (requireActive && reflex.state !== "active") return { ok: false, summary: "not_active" };
    if (!requireActive && reflex.state === "paused") return { ok: false, summary: "not_active" };
    const forbidden = REFLEX_FORBIDDEN.some((item) => reflex.capabilities.includes(item));
    if (forbidden || !REFLEX_CAPABILITIES.every((item) => reflex.capabilities.includes(item))) {
      return { ok: false, summary: "sensitive_blocked" };
    }
    const episode = await this.episode(episodeId);
    if (!episode || episode.classification !== "job_application") return { ok: false, summary: "not_running" };
    const settings = await this.settings();
    if (!settings.masterResumePath || !this.deps.files) return { ok: false, summary: "master_unreadable" };
    let master = "";
    try {
      master = await this.deps.files.readText(settings.masterResumePath);
    } catch {
      return { ok: false, summary: "master_unreadable" };
    }
    if (!master.trim()) return { ok: false, summary: "master_unreadable" };
    const observations = (await this.observations()).filter((item) => item.episodeId === episodeId);
    const description = (await Promise.all(observations.map((item) => this.observationText(item)))).filter(Boolean).join("\n");
    const documents = tailorDocuments(master, {
      company: episode.company ?? "the company",
      position: episode.position ?? "the role",
      description: [description, ...reflex.preferences].join("\n"),
    });
    return { ok: true, ...documents, company: episode.company ?? "job" };
  }

  private async setReflexState(state: "active" | "paused"): Promise<{ ok: boolean; summary: string }> {
    const reflex = await this.reflex();
    if (!reflex) return { ok: false, summary: "not_active" };
    if (state === "active" && reflex.state === "paused") {
      /* resume is allowed */
    } else if (state === "active" && reflex.state !== "shadow" && reflex.state !== "active") {
      return { ok: false, summary: "not_active" };
    }
    const next: WorkflowReflexRecord = {
      ...reflex,
      state,
      history: [...reflex.history, { version: reflex.version, at: this.now(), note: state === "active" ? "User activated this version." : "User paused this version." }],
    };
    await this.deps.records.put(REFLEX, next.reflexId, next.version, next, this.now());
    await this.trace("policy.evaluated", "policy.evaluate", state === "active" ? "reflex_activated" : "observation_paused", "completed", undefined, next.reflexId);
    return { ok: true, summary: state === "active" ? "reflex_active" : "reflex_paused" };
  }

  private async recordOutcome(episodeId: string, outcome: "submitted" | "in_progress" | "research"): Promise<{ ok: boolean; summary: string }> {
    const episode = await this.episode(episodeId);
    if (!episode) return { ok: false, summary: "not_running" };
    const next: WorkEpisodeRecord = {
      ...episode,
      version: episode.version + 1,
      outcome,
      classification: outcome === "research" ? "job_research" : episode.classification === "uncertain" ? "job_application" : episode.classification,
      locked: true,
      rationale: `${episode.rationale} Outcome recorded by you: ${outcome}.`,
    };
    await this.deps.records.put(EPISODE, next.episodeId, next.version, next, this.now());
    await this.trace("outcome.recorded", "episode.complete", "episode_recorded", "completed", episodeId);
    return { ok: true, summary: "outcome_recorded" };
  }

  private async recordCorrection(episodeId: string, before: string, after: string): Promise<{ ok: boolean; summary: string }> {
    const change = phraseChange(before, after);
    if (!change) return { ok: true, summary: "correction_noted" };
    const record: CorrectionRecord = {
      correctionId: this.deps.ids.next("edit"),
      episodeId,
      removed: change.removed,
      added: change.added,
      at: this.now(),
    };
    await this.deps.records.put(CORRECTION, record.correctionId, 1, record, this.now());
    const all = await this.corrections();
    const key = `${change.removed.toLowerCase()}=>${change.added.toLowerCase()}`;
    const matches = all.filter((item) => `${item.removed.toLowerCase()}=>${item.added.toLowerCase()}` === key);
    if (matches.length < 3) return { ok: true, summary: "correction_noted" };
    const existing = (await this.proposals()).find((item) => item.kind === "improvement" && item.examples[0] === key);
    if (existing && existing.state !== "pending") return { ok: true, summary: "correction_noted" };
    const proposal: WorkflowProposal = {
      proposalId: existing?.proposalId ?? this.deps.ids.next("proposal"),
      kind: "improvement",
      state: "pending",
      title: "Draft wording preference",
      explanation: `The user tends to replace "${change.removed}" with "${change.added}". Evidence: ${matches.length} similar edits.`,
      episodeIds: matches.map((item) => item.episodeId),
      examples: [key, ...matches.slice(0, 3).map((item) => item.episodeId)],
      updatedAt: this.now(),
    };
    await this.saveProposal(proposal);
    await this.trace("candidate.updated", "proposal.create", "pattern_proposed");
    return { ok: true, summary: "improvement_proposed" };
  }

  private async decideImprovement(
    proposalId: string,
    decision: "accept" | "reject" | "suppress" | "edit",
    preference?: string,
  ): Promise<{ ok: boolean; summary: string }> {
    const current = await this.reflex();
    const proposal = (await this.proposals()).find((item) => item.proposalId === proposalId && item.kind === "improvement");
    if (!proposal || proposal.state !== "pending") return { ok: false, summary: "not_proposed" };
    if (decision === "reject" || decision === "suppress") {
      await this.saveProposal({ ...proposal, state: decision === "suppress" ? "suppressed" : "rejected", updatedAt: this.now() });
      await this.trace("candidate.rejected", "proposal.create", "user_reject");
      return { ok: true, summary: current ? `reflex_unchanged_v${current.version}` : "reflex_unchanged" };
    }
    if (!current) return { ok: false, summary: "not_active" };
    const text = (decision === "edit" ? preference : preference || proposal.explanation)?.trim();
    if (!text) return { ok: false, summary: "malformed_observation" };
    const next: WorkflowReflexRecord = {
      ...current,
      version: current.version + 1,
      preferences: [...current.preferences, text],
      history: [...current.history, { version: current.version + 1, at: this.now(), note: "User approved a wording preference." }],
    };
    await this.deps.records.put(REFLEX, next.reflexId, next.version, next, this.now());
    await this.saveProposal({ ...proposal, state: "accepted", updatedAt: this.now() });
    await this.trace("candidate.approved", "proposal.create", "user_approval", "completed", undefined, next.reflexId);
    return { ok: true, summary: `reflex_v${next.version}` };
  }

  private async deleteObservations(): Promise<{ ok: boolean; summary: string }> {
    const rows = await this.deps.records.list(OBSERVATION);
    const at = this.now();
    for (const row of rows) {
      await this.forgetPayload(row.payload);
      await this.deps.records.put(OBSERVATION, row.id, row.version + 1, { deleted: true }, at);
    }
    const preview = await this.deps.records.get(PREVIEW, LOCAL);
    if (preview) await this.forgetPayload(preview.payload);
    this.shadow = null;
    await this.deps.records.put(PREVIEW, LOCAL, 1, null, at);
    const settings = await this.settings();
    await this.deps.records.put(SETTINGS, LOCAL, 1, { ...settings, historyDeletedAt: at }, at);
    await this.trace("policy.evaluated", "policy.evaluate", "history_deleted");
    return { ok: true, summary: `deleted_${rows.length}` };
  }

  private async setMaster(path: string): Promise<{ ok: boolean; summary: string }> {
    const trimmed = path.trim();
    if (!trimmed || trimmed.includes("\0") || !/\.(txt|md|docx)$/i.test(trimmed)) return { ok: false, summary: "malformed_observation" };
    const settings = await this.settings();
    await this.deps.records.put(SETTINGS, LOCAL, 1, { ...settings, masterResumePath: trimmed }, this.now());
    return { ok: true, summary: "master_set" };
  }

  private async judgeEpisode(episodeId: string): Promise<{ ok: boolean; summary: string }> {
    const episode = await this.episode(episodeId);
    if (!episode) return { ok: false, summary: "not_running" };
    if (episode.classification !== "uncertain") return { ok: false, summary: "not_ambiguous" };
    if (!this.deps.judge) return { ok: false, summary: "jev_unavailable" };
    const judged = await this.deps.judge(episodeId);
    if (!judged.ok) return { ok: false, summary: judged.reason };
    const gate = evaluateChoiceGate({ probabilities: judged.probabilities, minimum: 0.55, marginMinimum: 0.1 });
    const allowed = new Set(["job_research", "job_application", "other", "no_match"]);
    const selected = gate.pass && allowed.has(gate.selected) ? gate.selected : null;
    await this.deps.records.put(
      JUDGMENT,
      LOCAL,
      1,
      {
        episodeId,
        options: ["job_research", "job_application", "other", "no_match"],
        selected,
        probabilities: judged.probabilities,
        threshold: 0.55,
        reasonCode: gate.reasonCode,
      },
      this.now(),
    );
    await this.trace("judgment.completed", "judgment.response", gate.reasonCode, gate.pass ? "completed" : "failed", episodeId);
    if (!selected || selected === "no_match" || selected === "other") return { ok: true, summary: "judgment_kept_uncertain" };
    const next: WorkEpisodeRecord = {
      ...episode,
      version: episode.version + 1,
      classification: selected as EpisodeClassification,
      outcome: selected === "job_research" ? "research" : "in_progress",
      rationale: `${episode.rationale} Jev selected ${selected} at ${gate.top.toFixed(2)} (threshold 0.55, ${gate.reasonCode}).`,
      locked: false,
    };
    await this.deps.records.put(EPISODE, next.episodeId, next.version, next, this.now());
    return { ok: true, summary: `judged_${selected}` };
  }

  private async expire(): Promise<void> {
    const settings = await this.settings();
    const cutoff = Date.parse(this.now()) - settings.retentionDays * 24 * 60 * 60 * 1000;
    for (const row of await this.deps.records.list(OBSERVATION)) {
      const observation = row.payload as StoredObservation;
      if (!observation?.observedAt) continue;
      if (Date.parse(observation.observedAt) < cutoff || Date.parse(observation.retentionUntil) < Date.parse(this.now())) {
        await this.forgetPayload(row.payload);
        await this.deps.records.put(OBSERVATION, row.id, row.version + 1, { deleted: true }, this.now());
      }
    }
  }

  private async duplicate(observation: Omit<StoredObservation, "observationId" | "permissionReceiptId" | "episodeId">): Promise<boolean> {
    const at = Date.parse(observation.observedAt);
    const existing = await this.observations();
    if (observation.eventType === "window.focus") {
      const latest = [...existing].sort((a, b) => Date.parse(b.observedAt) - Date.parse(a.observedAt))[0];
      if (latest && latest.eventType === "window.focus" && latest.title === observation.title && latest.application === observation.application) {
        return true;
      }
    }
    return existing.some((item) => {
      return (
        item.eventType === observation.eventType &&
        item.url === observation.url &&
        item.title === observation.title &&
        item.application === observation.application &&
        Math.abs(Date.parse(item.observedAt) - at) < 8_000
      );
    });
  }

  private shadowReflex(note: string): WorkflowReflexRecord {
    return {
      reflexId: "reflex.job-application",
      version: 1,
      state: "shadow",
      trigger: "A job-application episode is open and a master resume is approved.",
      capabilities: [...REFLEX_CAPABILITIES],
      forbidden: [...REFLEX_FORBIDDEN],
      procedure: [
        "Read the approved master resume.",
        "Read the job description captured for this episode.",
        "Select resume lines that overlap the posting.",
        "Do not invent credentials, skills, or employers.",
        "Allow a title change only when the resume already uses a matching role word.",
        "Write a new resume copy and a cover letter.",
        "Leave the master resume unchanged.",
        "Do not submit, fill forms, or send email.",
      ],
      preferences: [],
      approval: "always_ask",
      history: [{ version: 1, at: this.now(), note }],
      runs: [],
    };
  }

  private async settings(): Promise<WorkflowSettings> {
    const row = await this.deps.records.get(SETTINGS, LOCAL);
    return row ? { ...DEFAULT_SETTINGS, ...(row.payload as WorkflowSettings) } : DEFAULT_SETTINGS;
  }

  private async host(): Promise<WorkflowHostStatus> {
    const row = await this.deps.records.get(HOST, LOCAL);
    return row ? { ...DEFAULT_HOST, ...(row.payload as WorkflowHostStatus) } : DEFAULT_HOST;
  }

  private async loadPreview(): Promise<WorkflowView["shadowPreview"]> {
    const row = await this.deps.records.get(PREVIEW, LOCAL);
    const payload = row?.payload as {
      resumeRef?: { artifactId: string; sha256: string };
      letterRef?: { artifactId: string; sha256: string };
    } | null;
    if (!payload?.resumeRef || !payload.letterRef || !this.deps.artifacts) return null;
    try {
      const resume = new TextDecoder().decode(await this.deps.artifacts.get({ ...payload.resumeRef, policy: localOnlyPolicy() }));
      const coverLetter = new TextDecoder().decode(await this.deps.artifacts.get({ ...payload.letterRef, policy: localOnlyPolicy() }));
      return { resume, coverLetter, notClaimed: [] };
    } catch {
      return null;
    }
  }

  private async forgetPayload(payload: unknown): Promise<void> {
    if (!payload || typeof payload !== "object" || !this.deps.forget) return;
    const record = payload as {
      excerptRef?: { artifactId?: string };
      resumeRef?: { artifactId?: string };
      letterRef?: { artifactId?: string };
    };
    for (const ref of [record.excerptRef, record.resumeRef, record.letterRef]) {
      if (ref?.artifactId) await this.deps.forget(ref.artifactId);
    }
  }

  private async seal(observation: StoredObservation): Promise<StoredObservation> {
    if (!observation.excerpt || !this.deps.artifacts) return observation;
    const ref = await this.deps.artifacts.put(new TextEncoder().encode(observation.excerpt), localOnlyPolicy());
    return { ...observation, excerpt: null, excerptRef: { artifactId: ref.artifactId, sha256: ref.sha256 } };
  }

  private async observationText(observation: StoredObservation): Promise<string> {
    if (observation.excerpt) return observation.excerpt;
    if (!observation.excerptRef || !this.deps.artifacts) return observation.title ?? "";
    try {
      const bytes = await this.deps.artifacts.get({ ...observation.excerptRef, policy: localOnlyPolicy() });
      return new TextDecoder().decode(bytes);
    } catch {
      return observation.title ?? "";
    }
  }

  private async observations(): Promise<StoredObservation[]> {
    const rows = await this.deps.records.list(OBSERVATION);
    return rows
      .map((row) => row.payload as StoredObservation & { deleted?: boolean })
      .filter((item) => item && !item.deleted && typeof item.observationId === "string");
  }

  private async episodes(): Promise<WorkEpisodeRecord[]> {
    return (await this.deps.records.list(EPISODE)).map((row) => row.payload as WorkEpisodeRecord);
  }

  private async episode(id: string): Promise<WorkEpisodeRecord | null> {
    const row = await this.deps.records.get(EPISODE, id);
    return row ? (row.payload as WorkEpisodeRecord) : null;
  }

  private async proposals(): Promise<WorkflowProposal[]> {
    return (await this.deps.records.list(PROPOSAL)).map((row) => row.payload as WorkflowProposal);
  }

  private async saveProposal(proposal: WorkflowProposal): Promise<void> {
    await this.deps.records.put(PROPOSAL, proposal.proposalId, 1, proposal, this.now());
  }

  private async reflex(): Promise<WorkflowReflexRecord | null> {
    const row = await this.deps.records.get(REFLEX, "reflex.job-application");
    return row ? (row.payload as WorkflowReflexRecord) : null;
  }

  private async corrections(): Promise<CorrectionRecord[]> {
    return (await this.deps.records.list(CORRECTION)).map((row) => row.payload as CorrectionRecord);
  }

  private now(): string {
    return this.deps.clock.now().toISOString();
  }

  private async trace(
    type: string,
    stage: string,
    reasonCode: string,
    status: "completed" | "failed" = "completed",
    episodeId?: string,
    reflexId?: string,
  ): Promise<void> {
    await this.deps.trace({
      type,
      stage,
      status,
      reasonCode,
      ...(episodeId ? { episodeId } : {}),
      ...(reflexId ? { reflexId } : {}),
    });
  }
}
