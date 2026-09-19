const STOP = new Set([
  "A",
  "I",
  "OK",
  "ID",
  "TV",
  "PC",
  "US",
  "UK",
  "AI",
]);

/** Deterministic acronym token detector — no model calls. */
export function detectAcronymTokens(text: string): { token: string; start: number; end: number }[] {
  const out: { token: string; start: number; end: number }[] = [];
  const re = /\b[A-Z]{2,8}\b/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(text)) !== null) {
    const token = match[0]!;
    if (STOP.has(token)) continue;
    out.push({ token, start: match.index, end: match.index + token.length });
  }
  return out;
}
