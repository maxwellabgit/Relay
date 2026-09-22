import type { MemoryView } from "@relay/contracts";

const TITLES: Readonly<Record<MemoryView["kind"], string>> = {
  note: "Note",
  fact: "Fact",
  recommendation: "Next",
  glossary: "Glossary",
  birthday: "Birthday",
};

/** Consumer library line. Shows saved prose, never the internal storage key. */
export function memoryLibraryLabel(memory: MemoryView): string {
  const prose = memory.fields.text || memory.fields.expansion || memory.fields.displayName || "";
  const title = TITLES[memory.kind];
  return prose ? `${title}: ${prose}` : title;
}
