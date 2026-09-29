import { describe, expect, it } from "vitest";
import { reconstructJobEpisode } from "./service.js";

describe("job application replay", () => {
  it("reconstructs one job application episode and does not invoke tools", () => {
    const episode = reconstructJobEpisode(
      [{ projectCaseId: "case_job", alias: "Job Search", intent: "Find a role." }],
      { next: (prefix) => `${prefix}_job` },
    );
    expect(episode).not.toBeNull();
    expect(episode?.origin).toBe("replay");
    expect(episode?.classification.label).toBe("job_application");
    expect(episode?.applications).toEqual(expect.arrayContaining(["Chrome", "Microsoft Word"]));
    expect(episode?.resources.map((item) => item.label)).toEqual(
      expect.arrayContaining(["Senior ML Engineer — Company X", "resume.docx", "resume-company-x.docx"]),
    );
    expect(episode?.evidence.map((item) => item.kind)).toEqual(
      expect.arrayContaining(["job_posting", "job_description", "resume", "application_page"]),
    );
    expect(episode?.caseId).toBe("case_job");
    expect(episode?.caseCandidateLabel).toBe("Job Search");
    expect(episode?.sequence.length).toBeGreaterThan(1);
    expect(episode?.classification.why.length).toBeGreaterThan(0);
  });
});
