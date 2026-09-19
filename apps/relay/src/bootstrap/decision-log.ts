import { DECISION_LOG_PATH, isKeptDecision, type DecisionLogPort } from "@relay/engine";

export function createBrowserDecisionLog(): DecisionLogPort {
  return {
    directoryLabel: DECISION_LOG_PATH,
    async append(record) {
      if (!isKeptDecision(record)) return;
      try {
        await fetch("/__relay/decisions", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            sequence: record.sequence,
            at: record.at,
            code: record.code,
            detail: record.detail,
          }),
        });
      } catch {
        // The in-memory ledger still has the line.
      }
    },
    async replace(records) {
      try {
        await fetch("/__relay/decisions", {
          method: "PUT",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(records.filter(isKeptDecision)),
        });
      } catch {
        // Dropping an empty session must not fail the engine.
      }
    },
    async read() {
      try {
        const response = await fetch("/__relay/decisions");
        if (!response.ok) return [];
        const parsed: unknown = await response.json();
        if (!Array.isArray(parsed)) return [];
        return parsed.filter(isKeptDecision);
      } catch {
        return [];
      }
    },
  };
}
