import type { EngineStore } from "../store.js";

const ROUND = "jev.semantic_round";
export const MAX_SEMANTIC_ROUNDS = 2;

type RoundEvent = {
  readonly caseId: string;
  readonly questionSetId: string;
  readonly round: number;
  readonly terminal: boolean;
  readonly reason?: string;
};

export async function claimSemanticRound(
  store: Pick<EngineStore, "appendDomainEvent" | "listDomainEvents">,
  caseId: string,
  questionSetId: string,
  at: string,
): Promise<{ ok: true; round: number } | { ok: false; reason: "max_semantic_rounds" }> {
  const events = await listRounds(store, caseId);
  const sameQuestion = events.find((event) => !event.terminal && event.questionSetId === questionSetId);
  if (sameQuestion) return { ok: true, round: sameQuestion.round };
  const used = events.filter((event) => !event.terminal);
  if (used.length >= MAX_SEMANTIC_ROUNDS) {
    if (!events.some((event) => event.terminal)) {
      await store.appendDomainEvent(ROUND, at, {
        caseId,
        questionSetId,
        round: used.length + 1,
        terminal: true,
        reason: "max_semantic_rounds",
      });
    }
    return { ok: false, reason: "max_semantic_rounds" };
  }
  const round = used.length + 1;
  await store.appendDomainEvent(ROUND, at, {
    caseId,
    questionSetId,
    round,
    terminal: false,
  });
  return { ok: true, round };
}

async function listRounds(
  store: Pick<EngineStore, "listDomainEvents">,
  caseId: string,
): Promise<RoundEvent[]> {
  const events = await store.listDomainEvents(8000);
  const rounds: RoundEvent[] = [];
  for (const event of events) {
    if (event.type !== ROUND || event.payload.caseId !== caseId) continue;
    if (typeof event.payload.questionSetId !== "string" || typeof event.payload.round !== "number") continue;
    rounds.push({
      caseId,
      questionSetId: event.payload.questionSetId,
      round: event.payload.round,
      terminal: event.payload.terminal === true,
      ...(typeof event.payload.reason === "string" ? { reason: event.payload.reason } : {}),
    });
  }
  return rounds;
}
