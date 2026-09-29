const STOP = new Set(
  "a an the and or for with from that this your you our their into onto over under about than then job role work using use will can may".split(" "),
);

export type TailoredDocuments = {
  readonly resume: string;
  readonly coverLetter: string;
  readonly claimed: readonly string[];
  readonly notClaimed: readonly string[];
};

/** Copies supported resume lines. Posting text never becomes experience. */
export function tailorDocuments(master: string, job: { readonly company: string; readonly position: string; readonly description: string }): TailoredDocuments {
  const lines = master.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const heading = lines[0] ?? "Resume";
  const bullets = lines.slice(1).filter((line) => line.length > 20);
  const requirements = job.description
    .split(/\r?\n|(?<=[.!?])\s+/)
    .map((line) => line.trim())
    .filter((line) => line.length > 12)
    .filter((line) => !/ignore (all|previous|above) instructions/i.test(line))
    .filter((line) => !/^\s*(tool|command|execute)\b/i.test(line));
  const claimed: string[] = [];
  const used = new Set<string>();
  for (const requirement of requirements) {
    const words = contentWords(requirement);
    const match = bullets.find((bullet) => {
      const have = new Set(contentWords(bullet));
      return words.filter((word) => have.has(word)).length >= Math.min(2, Math.max(1, words.length));
    });
    if (match && !used.has(match)) {
      used.add(match);
      claimed.push(match.replace(/^[-*]\s*/, ""));
    }
  }
  const notClaimed = requirements.filter((requirement) => {
    const words = contentWords(requirement);
    return !claimed.some((bullet) => {
      const have = new Set(contentWords(bullet));
      return words.filter((word) => have.has(word)).length >= Math.min(2, Math.max(1, words.length));
    });
  });
  const resumeTitle = titleAllowed(lines, job.position) ? job.position : heading;
  const resume = [
    resumeTitle,
    "",
    `Tailored for ${job.company} — ${job.position}.`,
    "Only lines supported by the approved master resume are included.",
    "",
    ...claimed.map((line) => `- ${line}`),
    "",
    "Not claimed from verified experience:",
    ...(notClaimed.length ? notClaimed.map((line) => `- ${line}`) : ["- None listed."]),
    "",
  ].join("\n");
  const coverLetter = [
    `Dear ${job.company} hiring team,`,
    "",
    `I am applying for ${job.position}. The points below are taken from my verified resume.`,
    "",
    ...claimed.map((line) => `- ${line}`),
    "",
    "I am not claiming requirements that are absent from that resume.",
    "",
    "Sincerely",
    "",
  ].join("\n");
  return { resume, coverLetter, claimed, notClaimed };
}

export function phraseChange(before: string, after: string): { readonly removed: string; readonly added: string } | null {
  const left = before.split(/\s+/).filter(Boolean);
  const right = after.split(/\s+/).filter(Boolean);
  let start = 0;
  while (start < left.length && start < right.length && left[start] === right[start]) start += 1;
  let endLeft = left.length - 1;
  let endRight = right.length - 1;
  while (endLeft >= start && endRight >= start && left[endLeft] === right[endRight]) {
    endLeft -= 1;
    endRight -= 1;
  }
  const removed = left.slice(start, endLeft + 1).join(" ");
  const added = right.slice(start, endRight + 1).join(" ");
  const removedWords = removed.split(" ").filter(Boolean).length;
  const addedWords = added.split(" ").filter(Boolean).length;
  if (!removed || !added) return null;
  if (removedWords < 2 || addedWords < 2 || removedWords > 12 || addedWords > 12) return null;
  return { removed, added };
}

function titleAllowed(lines: readonly string[], position: string): boolean {
  const wanted = contentWords(position);
  return lines.some((line) => {
    const have = new Set(contentWords(line));
    return wanted.some((word) => have.has(word));
  });
}

function contentWords(value: string): string[] {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .split(/\s+/)
    .filter((word) => word.length >= 4 && !STOP.has(word));
}
