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
  "THE",
  "AND",
  "FOR",
  "NOT",
  "BUT",
  "YOU",
  "ARE",
  "WAS",
  "HAS",
  "HAD",
  "DID",
  "DOES",
  "WHAT",
  "WHEN",
  "WHERE",
  "WHY",
  "HOW",
  "WHO",
  "MEAN",
  "MEANS",
]);

/** Deterministic acronym token detector — no model calls. */
export function detectAcronymTokens(
  text: string,
  opts: { caseInsensitive?: boolean } = {},
): { token: string; start: number; end: number }[] {
  const out: { token: string; start: number; end: number }[] = [];
  const seen = new Set<string>();

  const push = (token: string, start: number, end: number) => {
    const normalized = token.toUpperCase();
    if (STOP.has(normalized) || seen.has(normalized)) return;
    seen.add(normalized);
    out.push({ token: normalized, start, end });
  };

  // Always detect strict all-caps tokens.
  const caps = /\b[A-Z]{2,8}\b/g;
  let match: RegExpExecArray | null;
  while ((match = caps.exec(text)) !== null) {
    push(match[0]!, match.index, match.index + match[0]!.length);
  }

  if (opts.caseInsensitive) {
    // Explicit Ask patterns: "what does api mean", "expand bess", "define HTTP"
    const askPatterns = [
      /\b(?:what\s+(?:does|is)|define|expand|meaning\s+of)\s+([A-Za-z]{2,8})\b/gi,
      /\b([A-Za-z]{2,8})\s+(?:mean|means|stand(?:s)?\s+for)\b/gi,
    ];
    for (const re of askPatterns) {
      let m: RegExpExecArray | null;
      while ((m = re.exec(text)) !== null) {
        const token = m[1]!;
        push(token, m.index, m.index + m[0]!.length);
      }
    }
  }

  return out;
}
