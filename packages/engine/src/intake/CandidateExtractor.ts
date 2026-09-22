import type {
  AmbientUrgencyReason,
  CandidateKind,
  SourceSliceRef,
  TextModelPort,
} from "@relay/contracts";
import { generateTyped } from "../model/typed-repair.js";
import { EXTRACT_CANDIDATE_PROMPT_V1 } from "../prompts/extract-candidate.v1.js";

export const CANDIDATE_EXTRACTOR_VERSION = "candidate-extract@1";

export type ExtractedCandidate = {
  readonly kind: CandidateKind;
  readonly subjectKey: string | null;
  readonly subjectRefs: readonly string[];
  readonly noteText: string | null;
  readonly urgencyReason: AmbientUrgencyReason | null;
  readonly invalid: boolean;
  readonly extractorVersion: string;
};

/**
 * Local extraction only — validates structure in code.
 * Prefer cheap heuristics; optional local model may draft structure but never decides route.
 */
export async function extractCandidate(input: {
  readonly text: string;
  readonly isAsk: boolean;
  readonly sourceSlice: SourceSliceRef;
  readonly model?: TextModelPort;
  readonly signal?: AbortSignal;
}): Promise<ExtractedCandidate> {
  const text = input.text.trim();
  if (!text) {
    return empty("none", true);
  }

  if (input.isAsk) {
    return {
      kind: "direct_request",
      subjectKey: subjectKeyFromText(text),
      subjectRefs: [],
      noteText: null,
      urgencyReason: null,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  const heuristic = extractHeuristic(text);
  if (!input.model) return heuristic;

  const signal = input.signal ?? new AbortController().signal;
  try {
    const generated = await generateTyped({
      model: input.model,
      signal,
      request: {
        taskKind: EXTRACT_CANDIDATE_PROMPT_V1.taskKind,
        promptVersion: EXTRACT_CANDIDATE_PROMPT_V1.promptVersion,
        prompt: EXTRACT_CANDIDATE_PROMPT_V1.build(text),
        maxTokens: 120,
        temperature: 0,
      },
      accept: (draft) => !parseModelExtraction(draft, text).invalid,
      repairPrompt: (invalidText) =>
        [
          "The previous candidate kind was not allowed.",
          "Return one line: kind: <allowed kind>.",
          `Previous: ${invalidText}`,
          `Text: ${text}`,
        ].join("\n"),
    });
    if (!generated.ok) {
      if (generated.reason === "invalid_response") return empty("none", true);
      return heuristic;
    }
    return parseModelExtraction(generated.text, text);
  } catch {
    return heuristic;
  }
}

function extractHeuristic(text: string): ExtractedCandidate {
  const urgencyReason = detectUrgency(text);
  const correction = parseCorrection(text);
  if (correction) {
    return {
      kind: "correction",
      subjectKey: subjectKeyFromText(correction.subject),
      subjectRefs: [correction.subject],
      noteText: correction.note,
      urgencyReason,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  const commitment = parseCommitment(text);
  if (commitment) {
    return {
      kind: "commitment",
      subjectKey: subjectKeyFromText(commitment),
      subjectRefs: [],
      noteText: commitment,
      urgencyReason,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  const claim = parseFactClaim(text);
  if (claim) {
    return {
      kind: "factual_claim",
      subjectKey: subjectKeyFromText(claim),
      subjectRefs: [],
      noteText: claim,
      urgencyReason,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  const openQ = parseOpenQuestion(text);
  if (openQ) {
    return {
      kind: "open_question",
      subjectKey: subjectKeyFromText(openQ),
      subjectRefs: [],
      noteText: openQ,
      urgencyReason,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  const durable = parseDurable(text);
  if (durable) {
    return {
      kind: "durable_information",
      subjectKey: subjectKeyFromText(durable.subject),
      subjectRefs: [durable.subject],
      noteText: durable.note,
      urgencyReason,
      invalid: false,
      extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
    };
  }

  // Ordinary chatter — ignore is the intended common outcome.
  return empty("none", false);
}

function parseCorrection(text: string): { subject: string; note: string } | null {
  const patterns = [
    /\b(?:actually|correction|to clarify)[:,]?\s+(.+?)\s+(?:is|are)\s+(.+)$/i,
    /\b(?:not|isn't|aren't|wasn't)\s+(.+?)\s*[,;]\s*(?:it'?s|it is|they'?re|they are)\s+(.+)$/i,
    /\b(?:update|change)[:,]?\s+(.+?)\s+(?:to|is now)\s+(.+)$/i,
  ];
  for (const pattern of patterns) {
    const m = pattern.exec(text.trim());
    if (!m?.[1] || !m[2]) continue;
    const subject = m[1].trim();
    const note = `${subject} → ${m[2].trim()}`;
    if (subject.length < 2 || note.length < 4) continue;
    return { subject, note };
  }
  return null;
}

function parseCommitment(text: string): string | null {
  const patterns = [
    /\b(?:i'?ll|i will|we'?ll|we will|let'?s|need to|have to|must)\s+(.{8,160})$/i,
    /\b(?:deadline|due)\s+(?:is|by|:)?\s*(.{4,80})$/i,
    /\b(?:action item|todo|to-do)[:,]?\s*(.{6,160})$/i,
  ];
  for (const pattern of patterns) {
    const m = pattern.exec(text.trim());
    if (m?.[1]?.trim()) return text.trim();
  }
  return null;
}

function parseFactClaim(text: string): string | null {
  const patterns = [
    /\b(?:is|are|was|were)\s+(?:exactly|precisely|always|never)\b/i,
    /\b(?:according to|the (?:rate|price|version|endpoint|url) (?:is|was))\b/i,
    /\b\d+(\.\d+)?%\b/,
    /\b(?:ships|launches|releases)\s+on\b/i,
  ];
  if (patterns.some((p) => p.test(text)) && text.length >= 12) return text.trim();
  return null;
}

function parseOpenQuestion(text: string): string | null {
  const t = text.trim();
  if (t.endsWith("?") && t.length >= 8) return t;
  if (/^(?:who|what|when|where|why|how|should|can|do|does|did)\b/i.test(t) && t.length >= 8) {
    return t;
  }
  return null;
}

function parseDurable(text: string): { subject: string; note: string } | null {
  const patterns = [
    /\b(?:remember|save|keep|note)[:,]?\s+(.+)$/i,
    /\b(?:the|our)\s+(?:current|canonical|official)\s+(.{3,60})\s+is\s+(.+)$/i,
    /\b(?:deployment target|config|constraint|decision)[:,]?\s*(.+)$/i,
    /\b(?:we decided|decision)[:,]?\s*(.+)$/i,
  ];
  for (const pattern of patterns) {
    const m = pattern.exec(text.trim());
    if (!m) continue;
    const note = text.trim();
    const subject = (m[1] ?? note).trim().slice(0, 80);
    if (subject.length < 3) continue;
    return { subject, note };
  }
  return null;
}

function detectUrgency(text: string): AmbientUrgencyReason | null {
  if (/\b(asap|immediately|urgent|right now|emergency)\b/i.test(text)) return "deadline";
  if (/\b(deadline|by (?:eod|eow|tonight|tomorrow|friday)|due (?:today|tonight))\b/i.test(text)) {
    return "deadline";
  }
  if (/\b(unsafe|security|breach|leak|pii|credential)\b/i.test(text)) return "safety";
  if (/\b(blocked|blocking|cannot ship|can't ship|stop the release)\b/i.test(text)) {
    return "blocking_decision";
  }
  if (/\b(delete|wipe|lost|data loss|irreversible)\b/i.test(text)) return "data_loss";
  return null;
}

function parseModelExtraction(raw: string, fallbackText: string): ExtractedCandidate {
  const line = raw.trim().split(/\r?\n/).find((l) => l.trim().length > 0) ?? "";
  const kindMatch = /\bkind\s*[:=]\s*([a-z_]+)/i.exec(line) ?? /\bkind\s*[:=]\s*([a-z_]+)/i.exec(raw);
  const kindRaw = (kindMatch?.[1] ?? "").toLowerCase();
  const allowed: CandidateKind[] = [
    "direct_request",
    "durable_information",
    "factual_claim",
    "correction",
    "commitment",
    "open_question",
    "acronym",
    "birthday",
    "none",
  ];
  if (!allowed.includes(kindRaw as CandidateKind)) {
    // Malformed model output → invalid, not a guessed fallback kind.
    return empty("none", true);
  }
  const kind = kindRaw as CandidateKind;
  if (kind === "none") return empty("none", false);
  const heuristic = extractHeuristic(fallbackText);
  return {
    ...heuristic,
    kind,
    invalid: false,
  };
}

function subjectKeyFromText(text: string): string {
  return text
    .normalize("NFKC")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .slice(0, 64);
}

function empty(kind: CandidateKind, invalid: boolean): ExtractedCandidate {
  return {
    kind,
    subjectKey: null,
    subjectRefs: [],
    noteText: null,
    urgencyReason: null,
    invalid,
    extractorVersion: CANDIDATE_EXTRACTOR_VERSION,
  };
}
