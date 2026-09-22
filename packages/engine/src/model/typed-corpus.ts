/**
 * Deterministic lower-layer corpus for local typed drafts.
 * This is not a device tournament and not a pinned runtime.
 */
export const LOCAL_TYPED_CORPUS = [
  {
    id: "commitment",
    text: "We will send the release report tomorrow.",
    invalid: "kind: banana",
    valid: "kind: commitment",
    kind: "commitment",
  },
  {
    id: "open-question",
    text: "Should we ship the candidate this week?",
    invalid: "not a kind line",
    valid: "kind: open_question",
    kind: "open_question",
  },
  {
    id: "factual-claim",
    text: "The release candidate is exactly version 1.",
    invalid: "kind: ???",
    valid: "kind: factual_claim",
    kind: "factual_claim",
  },
  {
    id: "chatter",
    text: "Hello there.",
    invalid: "kind: nope",
    valid: "kind: none",
    kind: "none",
  },
] as const;
