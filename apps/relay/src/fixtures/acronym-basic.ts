import type { TranscriptEvent } from "@relay/contracts";

/** Shared acronym-basic fixture events. Same SourceEvent shape as JSONL fixtures. */
export const ACRONYM_BASIC_EVENTS: readonly TranscriptEvent[] = [
  {
    type: "segment.final",
    atMs: 0,
    segment: {
      schemaVersion: 1,
      sourceId: "fixture",
      sessionId: "session_web",
      segmentId: "seg_1",
      revision: 1,
      sequence: 1,
      startMs: 0,
      endMs: 1200,
      speakerKey: "SPEAKER_00",
      speakerConfidence: 0.92,
      text: "We should check the API before launch.",
      textConfidence: 0.95,
      final: true,
      origin: "scripted_transcript",
      cursor: null,
    },
  },
  {
    type: "segment.final",
    atMs: 1800,
    segment: {
      schemaVersion: 1,
      sourceId: "fixture",
      sessionId: "session_web",
      segmentId: "seg_2",
      revision: 1,
      sequence: 2,
      startMs: 1800,
      endMs: 3200,
      speakerKey: "SPEAKER_01",
      speakerConfidence: 0.9,
      text: "API means Application Programming Interface in our glossary.",
      textConfidence: 0.96,
      final: true,
      origin: "scripted_transcript",
      cursor: null,
    },
  },
];
