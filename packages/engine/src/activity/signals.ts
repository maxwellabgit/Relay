import type { Observation } from "@relay/contracts";

const BROWSERS = new Set(["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe"]);
const EDITORS = new Set(["cursor.exe", "code.exe", "devenv.exe"]);

export type ActivitySignals = {
  readonly jobPosting: boolean;
  readonly careers: boolean;
  readonly applicationPage: boolean;
  readonly resume: boolean;
  readonly jobDescription: boolean;
  readonly development: boolean; // pragma: allowlist secret
};

export function applicationLabel(processName: string | undefined): string {
  const name = (processName ?? "").toLowerCase();
  const known: Record<string, string> = {
    "chrome.exe": "Chrome",
    "msedge.exe": "Edge",
    "firefox.exe": "Firefox",
    "brave.exe": "Brave",
    "winword.exe": "Microsoft Word",
    "cursor.exe": "Cursor",
    "code.exe": "VS Code",
    "explorer.exe": "Explorer",
    "relay-desktop.exe": "RELAY",
  };
  if (known[name]) return known[name];
  if (name.includes("relay")) return "RELAY";
  if (!name) return "Unknown";
  return processName ?? "Unknown";
}

export function fileFromTitle(title: string): string | null {
  const match = /([^\\/:*?"<>|\r\n]{1,120}\.(?:docx|doc|pdf|txt|md|xlsx|pptx))/i.exec(title);
  return match?.[1] ?? null;
}

export function readSignals(observation: Observation): ActivitySignals {
  const title = `${observation.application?.windowTitle ?? ""} ${observation.resource?.title ?? ""}`;
  const heading = typeof observation.data?.heading === "string" ? observation.data.heading : "";
  const text = typeof observation.data?.text === "string" ? observation.data.text : "";
  const uri = observation.resource?.uri ?? "";
  const domain = observation.resource?.domain ?? "";
  const path = `${observation.resource?.path ?? ""} ${fileFromTitle(observation.application?.windowTitle ?? "") ?? ""}`;
  const processName = (observation.application?.processName ?? "").toLowerCase();
  const jobHost =
    /(^|\.)(jobs|careers)\./i.test(domain) ||
    /greenhouse\.io|lever\.co|myworkdayjobs\.com|smartrecruiters\.com|ashbyhq\.com/i.test(domain);
  const jobPath = /\/(jobs?|careers|apply|application)(\/|$|\?)/i.test(uri);
  const role = /\b(engineer|designer|manager|scientist|analyst|developer|director)\b/i.test(`${title} ${heading}`);
  const careers = /\bcareers\b/i.test(`${title} ${heading} ${uri}`);
  const applicationPage = /\/apply(\/|$|\?)|\bapplication\b|\bapply\b/i.test(`${title} ${heading} ${uri}`);
  const resume = /\b(resume|curriculum vitae)\b|\bcv\.(?:docx|pdf|doc|txt)\b/i.test(path);
  const jobDescription =
    text.length > 80 && (jobHost || jobPath || /\b(qualifications|responsibilities|job description)\b/i.test(text));
  const jobPosting = jobHost || jobPath || (BROWSERS.has(processName) && role && (careers || / [-—|] /.test(title)));
  const development = EDITORS.has(processName) || /\.(?:ts|tsx|rs|py|js|cs)\b/.test(observation.application?.windowTitle ?? ""); // pragma: allowlist secret
  return { jobPosting, careers, applicationPage, resume, jobDescription, development }; // pragma: allowlist secret
}

export function jobRelated(signals: ActivitySignals): boolean {
  return signals.jobPosting || signals.careers || signals.applicationPage || signals.resume || signals.jobDescription;
}

export function threadFor(observation: Observation, signals: ActivitySignals): string {
  if (jobRelated(signals)) return "job";
  if (signals.development) return "dev"; // pragma: allowlist secret
  if (observation.resource?.domain) return `site:${observation.resource.domain}`;
  if (observation.resource?.path) return `file:${observation.resource.path.toLowerCase()}`;
  return `app:${(observation.application?.processName ?? "unknown").toLowerCase()}`;
}

export function sequenceToken(observation: Observation): string {
  const processName = (observation.application?.processName ?? observation.source.type).toLowerCase();
  const resource = observation.resource?.domain ?? observation.resource?.path ?? observation.application?.windowTitle ?? "";
  return `${observation.source.type}:${processName}:${resource}`.slice(0, 180);
}
