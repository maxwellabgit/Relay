export type TranscriptOrigin =
  | "typed"
  | "scripted_transcript"
  | "audio_file"
  | "microphone"
  | "wispr"
  | "halo";

export type TranscriptSegmentV1 = {
  readonly schemaVersion: 1;
  readonly sourceId: string;
  readonly sessionId: string;
  readonly segmentId: string;
  readonly revision: number;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerKey: string | null;
  readonly speakerConfidence: number | null;
  readonly text: string;
  readonly textConfidence: number | null;
  readonly final: boolean;
  readonly origin: TranscriptOrigin;
  readonly cursor: string | null;
};

export type SegmentInterimEvent = {
  readonly type: "segment.interim";
  readonly atMs: number;
  readonly segment: TranscriptSegmentV1;
};

export type SegmentFinalEvent = {
  readonly type: "segment.final";
  readonly atMs: number;
  readonly segment: TranscriptSegmentV1;
};

export type SegmentSpeakerRevisedEvent = {
  readonly type: "segment.speaker_revised";
  readonly atMs: number;
  readonly segmentId: string;
  readonly revision: number;
  readonly speakerKey: string;
  readonly speakerConfidence: number | null;
};

export type TranscriptEvent =
  | SegmentInterimEvent
  | SegmentFinalEvent
  | SegmentSpeakerRevisedEvent;

export type SourceSliceRef = {
  readonly artifactId: string;
  readonly sha256: string;
  readonly sourceEventId?: string;
  readonly segmentId?: string;
  readonly start: number;
  readonly end: number;
  readonly offsetsValidated: boolean;
};
