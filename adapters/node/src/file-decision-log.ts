import { appendFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { KeptDecision } from "@relay/contracts";
import { isKeptDecision, parseDecisionLog, type DecisionLogPort } from "@relay/engine";

export function createFileDecisionLog(directory: string): DecisionLogPort {
  const file = join(directory, "decisions.jsonl");
  return {
    directoryLabel: file,
    async append(record) {
      if (!isKeptDecision(record)) return;
      await mkdir(directory, { recursive: true });
      const line: KeptDecision = {
        sequence: record.sequence,
        at: record.at,
        code: record.code,
        detail: record.detail,
      };
      await appendFile(file, `${JSON.stringify(line)}\n`, "utf8");
    },
    async read() {
      try {
        return parseDecisionLog(await readFile(file, "utf8"));
      } catch {
        return [];
      }
    },
    async replace(records) {
      const kept = records.filter(isKeptDecision);
      await mkdir(directory, { recursive: true });
      const body = kept.length === 0 ? "" : `${kept.map((line) => JSON.stringify(line)).join("\n")}\n`;
      await writeFile(file, body, "utf8");
    },
  };
}
