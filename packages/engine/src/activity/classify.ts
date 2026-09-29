import type { EpisodeClassification, EpisodeEvidence, WorkEpisode } from "@relay/contracts";
import { JOB_APPLICATION_MIN_SPAN_MS } from "./bounds.js";

const DISPLAY: Record<EpisodeClassification["label"], string> = {
  job_application: "Job Application",
  job_research: "Job Research",
  general_browsing: "Browsing",
  development: "Development", // pragma: allowlist secret
  unknown: "Activity",
};

export function classificationDisplay(label: EpisodeClassification["label"]): string {
  return DISPLAY[label];
}

function has(evidence: readonly EpisodeEvidence[], kind: string): boolean {
  return evidence.some((item) => item.kind === kind);
}

/** First specialized classifier. Job application is not part of the Observation schema. */
export function classifyEpisode(episode: Pick<WorkEpisode, "evidence" | "startedAt" | "endedAt" | "observationIds" | "applications" | "resources">): EpisodeClassification {
  const evidence = episode.evidence;
  const jobPage = has(evidence, "job_posting") || has(evidence, "job_description");
  const resume = has(evidence, "resume");
  const application = has(evidence, "application_page");
  const end = Date.parse(episode.endedAt ?? episode.startedAt);
  const span = Math.max(0, end - Date.parse(episode.startedAt));
  const resources = episode.resources.length;
  const why: string[] = [];
  if (jobPage) why.push("Saw a job posting or careers page.");
  if (has(evidence, "job_description")) why.push("Read job description text.");
  if (resume) why.push("Worked in a resume document.");
  if (application) why.push("Opened an application page.");
  if (span >= JOB_APPLICATION_MIN_SPAN_MS) why.push("The sequence lasted several minutes.");
  if (
    jobPage &&
    (resume || application) &&
    resources >= 2 &&
    episode.observationIds.length >= 3 &&
    span >= JOB_APPLICATION_MIN_SPAN_MS
  ) {
    const complete = jobPage && resume && application;
    return {
      label: "job_application",
      confidence: complete ? 0.86 : 0.74,
      why,
    };
  }
  if (jobPage) {
    return {
      label: "job_research",
      confidence: has(evidence, "job_description") ? 0.66 : 0.52,
      why: why.length > 0 ? why : ["A job page was open without an application sequence."],
    };
  }
  if (has(evidence, "development") || episode.applications.some((name) => name === "Cursor" || name === "VS Code")) { // pragma: allowlist secret
    return {
      label: "development", // pragma: allowlist secret
      confidence: 0.7,
      why: ["The foreground work was in a code editor."],
    };
  }
  if (episode.applications.some((name) => name === "Chrome" || name === "Edge" || name === "Firefox" || name === "Brave")) {
    return {
      label: "general_browsing",
      confidence: 0.45,
      why: ["Browser activity did not match a specialized workflow."],
    };
  }
  return {
    label: "unknown",
    confidence: 0.2,
    why: ["RELAY kept the applications and titles without a specialized classification."],
  };
}
