import type { ArtifactRef, ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import type { MemoryKind } from "./learning-store.js";

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();

export type PackedMemory = {
  readonly prose: Readonly<Record<string, string>>;
  readonly metadata: Readonly<Record<string, string>>;
};

/** Split memory value into free prose (artifact) vs typed structural metadata (SQLite). */
export function packMemoryValue(kind: MemoryKind, value: Readonly<Record<string, string>>): PackedMemory {
  if (kind === "glossary") {
    const { expansion = "", status = "", ...rest } = value;
    const metadata: Record<string, string> = { ...rest };
    if (status) metadata.status = status;
    return { prose: expansion ? { expansion } : {}, metadata };
  }
  const { displayName = "", month = "", day = "", year = "", ...rest } = value;
  const metadata: Record<string, string> = { ...rest };
  if (month) metadata.month = month;
  if (day) metadata.day = day;
  if (year) metadata.year = year;
  return { prose: displayName ? { displayName } : {}, metadata };
}

export function unpackMemoryValue(
  kind: MemoryKind,
  prose: Readonly<Record<string, string>>,
  metadata: Readonly<Record<string, string>>,
): Record<string, string> {
  if (kind === "glossary") {
    return {
      ...metadata,
      ...(prose.expansion ? { expansion: prose.expansion } : {}),
    };
  }
  return {
    ...metadata,
    ...(prose.displayName ? { displayName: prose.displayName } : {}),
  };
}

export function encodeJson(value: unknown): Uint8Array {
  return textEncoder.encode(JSON.stringify(value));
}

export function decodeJson<T>(bytes: Uint8Array): T {
  return JSON.parse(textDecoder.decode(bytes)) as T;
}

export async function putJsonArtifact(artifacts: ArtifactStorePort, value: unknown): Promise<ArtifactRef> {
  return artifacts.put(encodeJson(value), localOnlyPolicy());
}

export async function getJsonArtifact<T>(artifacts: ArtifactStorePort, ref: ArtifactRef): Promise<T> {
  return decodeJson<T>(await artifacts.get(ref));
}

/** Map option ids to unavailable when protected labels cannot be hydrated. */
export function labelsOrUnavailable(
  optionIds: readonly string[],
  labels: Readonly<Record<string, string>> | null | undefined,
): Record<string, string> {
  if (labels && Object.keys(labels).length > 0) return { ...labels };
  const out: Record<string, string> = {};
  for (const id of optionIds) out[id] = "unavailable";
  return out;
}
