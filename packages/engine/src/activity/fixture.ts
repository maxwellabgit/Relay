import type { ActivitySettings } from "@relay/contracts";

const DAY = "2026-09-29";

function at(hours: number, minutes: number, seconds = 0): string {
  const hh = String(hours).padStart(2, "0");
  const mm = String(minutes).padStart(2, "0");
  const ss = String(seconds).padStart(2, "0");
  return `${DAY}T${hh}:${mm}:${ss}.000Z`;
}

export const JOB_APPLICATION_SETTINGS: ActivitySettings = {
  enabled: true,
  windowsEnabled: true,
  chromeEnabled: true,
  pageContentEnabled: true,
  permittedDomains: ["jobs.companyx.example", "companyx.example"],
  retentionDays: 7,
};

export function jobApplicationObservations(): readonly unknown[] {
  const description =
    "Senior ML Engineer job description. Responsibilities include modeling and evaluation. Qualifications include Python. Apply on this posting.";
  return [
    {
      timestamp: at(10, 18, 0),
      source: { type: "windows", provider: "setwineventhook" },
      eventType: "foreground.changed",
      application: { processName: "chrome.exe", windowTitle: "Senior ML Engineer — Company X - Google Chrome" },
    },
    {
      timestamp: at(10, 18, 5),
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe", windowTitle: "Senior ML Engineer — Company X" },
      resource: {
        uri: "https://jobs.companyx.example/1234",
        domain: "jobs.companyx.example",
        title: "Senior ML Engineer — Company X",
      },
      data: { heading: "Senior ML Engineer", text: description },
    },
    {
      timestamp: at(10, 22, 0),
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe" },
      resource: {
        uri: "https://companyx.example/careers",
        domain: "companyx.example",
        title: "Company X Careers",
      },
      data: { heading: "Careers", text: "Company X Careers. Open roles and application process." },
    },
    {
      timestamp: at(10, 31, 0),
      source: { type: "windows", provider: "setwineventhook" },
      eventType: "foreground.changed",
      application: { processName: "WINWORD.EXE", windowTitle: "resume.docx - Word" },
    },
    {
      timestamp: at(10, 36, 0),
      source: { type: "windows", provider: "setwineventhook" },
      eventType: "foreground.changed",
      application: { processName: "WINWORD.EXE", windowTitle: "resume-company-x.docx - Word" },
    },
    {
      timestamp: at(10, 47, 0),
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe" },
      resource: {
        uri: "https://jobs.companyx.example/1234/apply",
        domain: "jobs.companyx.example",
        title: "Company X Application",
      },
      data: { heading: "Application", text: "Application form for Senior ML Engineer. Do not submit during tests." },
    },
  ];
}
