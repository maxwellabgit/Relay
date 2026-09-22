import type { ArtifactRef, DataPolicy } from "./artifacts.js";
import type { GlassesDisplayPort } from "./glasses.js";
import type { JudgmentRequest, JudgmentResponse } from "./judgments.js";
import type { PublicSearchPort } from "./tools.js";
import type { TranscriptEvent } from "./transcript.js";

export type TranscriptSourcePort = {
  events(signal: AbortSignal): AsyncIterable<TranscriptEvent>;
};

export type RelayTransaction<T> = (tx: RelayTransactionContext) => Promise<T>;

export type RelayTransactionContext = {
  readonly execute: (sql: string, params?: readonly unknown[]) => Promise<unknown>;
};

export type RelayStorePort = {
  transaction<T>(fn: RelayTransaction<T>): Promise<T>;
};

export type ArtifactStorePort = {
  put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef>;
  get(ref: ArtifactRef): Promise<Uint8Array>;
};

export type JudgmentPort = {
  judge(request: JudgmentRequest, signal: AbortSignal): Promise<JudgmentResponse>;
};

export type GenerationRequest = {
  readonly taskKind: string;
  readonly promptVersion?: string;
  readonly prompt: string;
  readonly evidenceExcerpts?: readonly string[];
  readonly caseId?: string;
  readonly maxTokens?: number;
  readonly temperature?: number;
};

export type GenerationFailureReason =
  | "model_unavailable"
  | "timeout"
  | "invalid_response"
  | "cancelled"
  | "model_disabled"
  | string;

export type GenerationResponse =
  | { readonly ok: true; readonly text: string; readonly model: string; readonly elapsedMs: number }
  | { readonly ok: false; readonly failureReason: GenerationFailureReason };

export type TextModelPort = {
  generate(request: GenerationRequest, signal: AbortSignal): Promise<GenerationResponse>;
};

export type { GlassesDisplayPort };

export type RelayPorts = {
  readonly store: RelayStorePort;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly model: TextModelPort;
  readonly glasses: GlassesDisplayPort;
  readonly publicSearch?: PublicSearchPort;
};
