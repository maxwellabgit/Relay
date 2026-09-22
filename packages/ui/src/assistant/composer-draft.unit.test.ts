import { describe, expect, it } from "vitest";
import { canSendComposer, nextComposerDraft } from "./composer-draft.js";

describe("composer draft", () => {
  it("refuses an empty, busy, or already-sending composer", () => {
    expect(canSendComposer({ text: "  ", disabled: false, busy: false, sending: false })).toBe(false);
    expect(canSendComposer({ text: "hello", disabled: true, busy: false, sending: false })).toBe(false);
    expect(canSendComposer({ text: "hello", disabled: false, busy: true, sending: false })).toBe(false);
    expect(canSendComposer({ text: "hello", disabled: false, busy: false, sending: true })).toBe(false);
    expect(canSendComposer({ text: "hello", disabled: false, busy: false, sending: false })).toBe(true);
  });

  it("keeps the draft when the send is refused and clears it when that text is accepted", () => {
    expect(nextComposerDraft("plan the test", "plan the test", false)).toBe("plan the test");
    expect(nextComposerDraft("plan the test", "plan the test", true)).toBe("");
    expect(nextComposerDraft("plan the test now", "plan the test", true)).toBe("plan the test now");
  });
});
