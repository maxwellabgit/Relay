import type {
  EpisodeClassification,
  EpisodeEvidence,
  EpisodeOutcomeKind,
  StoredObservation,
} from "@relay/contracts";
import { hostname } from "./policy.js";

export const EPISODE_GAP_MS = 25 * 60 * 1000;

const JOB_URL = /jobs?|career|greenhouse|lever\.co|myworkday|ashbyhq|linkedin\.com\/jobs|indeed\.com|glassdoor|workday/i;
const CONFIRM = /application (received|submitted)|thank you for applying|we (have )?received your application/i;
const APPLY = /\b(apply|application)\b/i;
const RESUME = /resume|curriculum vitae|\bcv\b/i;
const DOC_APP = /winword|microsoft word|word|acrobat|writer|notepad|code/i;

export type Classification = {
  readonly classification: EpisodeClassification;
  readonly outcome: EpisodeOutcomeKind;
  readonly confidence: "high" | "medium" | "low";
  readonly rationale: string;
  readonly company: string | null;
  readonly position: string | null;
};

export function isJobListing(observation: StoredObservation): boolean {
  const url = observation.url ?? "";
  const title = observation.title ?? "";
  return JOB_URL.test(url) || (JOB_URL.test(title) && !CONFIRM.test(title));
}

export function isResumeActivity(observation: StoredObservation): boolean {
  return RESUME.test(`${observation.title ?? ""} ${observation.url ?? ""}`) || DOC_APP.test(observation.application ?? "");
}

export function isApplicationPage(observation: StoredObservation): boolean {
  const text = `${observation.url ?? ""} ${observation.title ?? ""}`;
  return APPLY.test(text) && !CONFIRM.test(text) && !isJobListing(observation);
}

export function isConfirmation(observation: StoredObservation): boolean {
  return CONFIRM.test(`${observation.title ?? ""} ${observation.url ?? ""}`);
}

export function classifyObservations(observations: readonly StoredObservation[]): Classification {
  const listing = observations.filter(isJobListing);
  const resume = observations.filter(isResumeActivity);
  const application = observations.filter(isApplicationPage);
  const confirmation = observations.filter(isConfirmation);
  const position = positionFrom(listing[0] ?? application[0] ?? observations[0]);
  const company = companyFrom(listing[0] ?? application[0] ?? observations[0]);
  if (listing.length > 0 && resume.length === 0 && application.length === 0 && confirmation.length === 0) {
    return {
      classification: "job_research",
      outcome: "research",
      confidence: "high",
      rationale: "A job listing was viewed. No resume or application activity was observed, so this is research, not a completed application.",
      company,
      position,
    };
  }
  if (confirmation.length > 0 && (listing.length > 0 || application.length > 0)) {
    return {
      classification: "job_application",
      outcome: "submitted",
      confidence: resume.length > 0 ? "high" : "medium",
      rationale: `A confirmation page was observed (${confirmation[0]?.title ?? "application received"}) after job activity. RELAY does not treat a listing view alone as submitted.`,
      company,
      position,
    };
  }
  if (listing.length > 0 && resume.length > 0) {
    return {
      classification: "job_application",
      outcome: "in_progress",
      confidence: application.length > 0 ? "high" : "medium",
      rationale: "Job listing activity and resume work occurred in the same period. No submission confirmation was observed.",
      company,
      position,
    };
  }
  if (listing.length > 0 && application.length > 0) {
    return {
      classification: "job_application",
      outcome: "in_progress",
      confidence: "low",
      rationale: "An application page was opened after a listing. Resume activity was not observed, and nothing was marked submitted.",
      company,
      position,
    };
  }
  if (listing.length > 0) {
    return {
      classification: "job_research",
      outcome: "research",
      confidence: "medium",
      rationale: "Job-related pages were viewed without a submission confirmation.",
      company,
      position,
    };
  }
  return {
    classification: "uncertain",
    outcome: "unknown",
    confidence: "low",
    rationale: "The activity does not match a known workflow. You can correct the classification.",
    company,
    position,
  };
}

export function related(existing: readonly StoredObservation[], next: StoredObservation): boolean {
  const gap = Date.parse(next.observedAt) - Date.parse(existing[existing.length - 1]?.observedAt ?? next.observedAt);
  if (gap > EPISODE_GAP_MS) return false;
  const nextHost = hostname(next.url);
  const hosts = existing.map((item) => hostname(item.url)).filter((item): item is string => Boolean(item));
  if (nextHost && hosts.includes(nextHost)) return true;
  if (isResumeActivity(next) && existing.some(isJobListing)) return true;
  if (isJobListing(next) && existing.some(isResumeActivity)) return true;
  if (isApplicationPage(next) && existing.some((item) => isJobListing(item) || isResumeActivity(item))) return true;
  if (isConfirmation(next) && existing.some((item) => isJobListing(item) || isApplicationPage(item))) return true;
  return false;
}

export function evidenceFor(observation: StoredObservation): EpisodeEvidence {
  return {
    observationId: observation.observationId,
    label: observation.title || observation.application || observation.eventType,
    application: observation.application,
    host: hostname(observation.url),
    at: observation.observedAt,
  };
}

export function companyFrom(observation: StoredObservation | undefined): string | null {
  if (!observation) return null;
  const title = observation.title ?? "";
  const parts = title.split(/\s+[—–|@]\s+|\s+ at \s+/i);
  if (parts.length > 1) return parts[parts.length - 1]?.trim() || null;
  const host = hostname(observation.url);
  if (!host) return null;
  const label = host.split(".").slice(-2, -1)[0] ?? host;
  return label.charAt(0).toUpperCase() + label.slice(1);
}

export function positionFrom(observation: StoredObservation | undefined): string | null {
  if (!observation?.title) return null;
  const parts = observation.title.split(/\s+[—–|@]\s+|\s+ at \s+/i);
  return parts[0]?.trim() || null;
}

export const PATTERN_MINIMUM = 3;

export function commonSteps(episodes: readonly { evidence: readonly EpisodeEvidence[]; classification: EpisodeClassification }[]): string[] {
  const steps = ["inspect job description"];
  if (episodes.some((episode) => episode.evidence.some((item) => RESUME.test(item.label)))) steps.push("open master resume");
  if (episodes.some((episode) => episode.evidence.some((item) => /resume|cv/i.test(item.label) && !/master/i.test(item.label)))) {
    steps.push("create tailored copy");
  }
  steps.push("submit application");
  return steps;
}
