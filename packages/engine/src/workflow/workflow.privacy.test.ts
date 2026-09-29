import { describe, expect, it } from "vitest";
import { acceptSignal } from "./policy.js";
import type { WorkflowSettings } from "@relay/contracts";

const settings: WorkflowSettings = {
  setupComplete: true,
  paused: false,
  windowsEnabled: true,
  chromeEnabled: true,
  pageContentEnabled: true,
  retentionDays: 7,
  allowedSites: ["jobs.example.com"],
  allowedFolders: [],
  masterResumePath: null,
  patternSuppressed: false,
};

describe("workflow privacy", () => {
  it("drops credential surfaces and refuses password material", () => {
    const login = acceptSignal(
      {
        observedAt: "2026-09-28T14:00:00.000Z",
        sourceType: "browser",
        provider: "chrome",
        eventType: "page.semantic",
        url: "https://jobs.example.com/login",
        title: "Sign in",
        excerpt: "password: hunter2",
      },
      { settings, sessionSites: [], now: "2026-09-28T14:00:00.000Z" },
    );
    expect(login.ok).toBe(false);

    const page = acceptSignal(
      {
        observedAt: "2026-09-28T14:00:00.000Z",
        sourceType: "browser",
        provider: "chrome",
        eventType: "browser.navigate",
        url: "https://jobs.example.com/login",
        title: "Sign in",
        excerpt: "account form",
      },
      { settings, sessionSites: [], now: "2026-09-28T14:00:00.000Z" },
    );
    expect(page.ok).toBe(true);
    if (page.ok) {
      expect(page.observation.excerpt).toBeNull();
      expect(page.observation.url).toBe("https://jobs.example.com/");
      expect(page.observation.title).toBe("[redacted]");
      expect(page.observation.sensitivity).toBe("credential_surface");
    }
    const secretUrl = acceptSignal(
      {
        observedAt: "2026-09-28T14:00:00.000Z",
        sourceType: "browser",
        provider: "chrome",
        eventType: "browser.navigate",
        url: "https://jobs.example.com/login?password=hunter2",
        title: "Sign in",
      },
      { settings, sessionSites: [], now: "2026-09-28T14:00:00.000Z" },
    );
    expect(secretUrl.ok).toBe(false);
    const accessToken = acceptSignal(
      {
        observedAt: "2026-09-28T14:00:00.000Z",
        sourceType: "browser",
        provider: "chrome",
        eventType: "browser.navigate",
        url: "https://jobs.example.com/cb?access_token=abc",
        title: "Callback",
      },
      { settings, sessionSites: [], now: "2026-09-28T14:00:00.000Z" },
    );
    expect(accessToken.ok).toBe(false);
  });
});
