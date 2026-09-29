import type { WorkEpisode } from "@relay/contracts";

export type CaseHint = {
  readonly projectCaseId: string;
  readonly alias: string;
  readonly intent: string;
};

const SEED_ALIASES = new Set(["acronyms", "birthdays", "self-improvement"]);

export type CaseAssociation = {
  readonly caseId: string | null;
  readonly caseConfidence: number | null;
  readonly caseEvidence: readonly string[];
  readonly caseCandidateLabel: string | null;
};

export function associateEpisode(episode: Pick<WorkEpisode, "classification" | "title" | "resources" | "manualCase" | "caseId" | "caseConfidence" | "caseEvidence" | "caseCandidateLabel">, cases: readonly CaseHint[]): CaseAssociation {
  if (episode.manualCase) {
    return {
      caseId: episode.caseId,
      caseConfidence: episode.caseConfidence,
      caseEvidence: episode.caseEvidence,
      caseCandidateLabel: episode.caseCandidateLabel,
    };
  }
  const label = episode.classification.label;
  if (label === "job_application" || label === "job_research") {
    const match = cases.find((item) => /job search/i.test(item.alias) || /job search/i.test(item.intent));
    return {
      caseId: match?.projectCaseId ?? null,
      caseConfidence: match ? 0.74 : 0.55,
      caseEvidence: ["Job activity sequence matches a Job Search case."],
      caseCandidateLabel: "Job Search",
    };
  }
  const haystack = [episode.title, ...episode.resources.map((item) => item.label)].join(" ").toLowerCase();
  for (const item of cases) {
    const alias = item.alias.trim().toLowerCase();
    if (alias.length < 6 || SEED_ALIASES.has(alias)) continue;
    const escaped = alias.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    if (new RegExp(`(^|[^a-z0-9])${escaped}([^a-z0-9]|$)`, "i").test(haystack)) {
      return {
        caseId: item.projectCaseId,
        caseConfidence: 0.6,
        caseEvidence: [`A resource title contains the Case alias ${item.alias}.`],
        caseCandidateLabel: item.alias,
      };
    }
  }
  return { caseId: null, caseConfidence: null, caseEvidence: [], caseCandidateLabel: null };
}
