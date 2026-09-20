import type { TranscriptEvent } from "@relay/contracts";

/** Canonical acronym-basic transcript events (mirrors fixtures/public/transcripts/acronym-basic.jsonl). */
export const ACRONYM_BASIC_JSONL = `{"v":1,"type":"segment.final","atMs":0,"segment":{"schemaVersion":1,"sourceId":"fixture","sessionId":"sess_acronym","segmentId":"seg_1","revision":1,"sequence":1,"startMs":0,"endMs":1200,"speakerKey":"SPEAKER_00","speakerConfidence":0.92,"text":"We should check the API before launch.","textConfidence":0.95,"final":true,"origin":"scripted_transcript","cursor":null}}
{"v":1,"type":"segment.final","atMs":1800,"segment":{"schemaVersion":1,"sourceId":"fixture","sessionId":"sess_acronym","segmentId":"seg_2","revision":1,"sequence":2,"startMs":1800,"endMs":3200,"speakerKey":"SPEAKER_01","speakerConfidence":0.9,"text":"API means Application Programming Interface in our glossary.","textConfidence":0.96,"final":true,"origin":"scripted_transcript","cursor":null}}
{"v":1,"type":"segment.speaker_revised","atMs":3500,"segmentId":"seg_1","revision":2,"speakerKey":"SPEAKER_00","speakerConfidence":0.98}`;

export function parseTranscriptJsonl(text: string): TranscriptEvent[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line) as TranscriptEvent);
}

export const ACRONYM_BASIC_EVENTS = parseTranscriptJsonl(ACRONYM_BASIC_JSONL);
