import type { EngineStore } from "../store.js";

const PARKED = "jev.wait_parked";
const RESUMED = "jev.wait_resumed";

export async function parkHostedWait(
  store: Pick<EngineStore, "appendDomainEvent" | "listDomainEvents">,
  at: string,
  caseId: string,
  payload: Record<string, unknown>,
): Promise<"parked" | "already"> {
  const events = await store.listDomainEvents(8000);
  if (events.some((event) => event.type === PARKED && event.payload.caseId === caseId)) return "already";
  await store.appendDomainEvent(PARKED, at, { caseId, payload });
  return "parked";
}

export async function listHostedWaits(
  store: Pick<EngineStore, "listDomainEvents">,
): Promise<readonly { caseId: string; payload: Record<string, unknown> }[]> {
  const events = await store.listDomainEvents(8000);
  const parked = new Map<string, Record<string, unknown>>();
  const resumed = new Set<string>();
  for (const event of events) {
    if (event.type === PARKED && typeof event.payload.caseId === "string") {
      const payload = event.payload.payload;
      if (payload && typeof payload === "object" && !Array.isArray(payload)) {
        parked.set(event.payload.caseId, payload as Record<string, unknown>);
      }
    } else if (event.type === RESUMED && typeof event.payload.caseId === "string") {
      resumed.add(event.payload.caseId);
    }
  }
  return [...parked.entries()]
    .filter(([caseId]) => !resumed.has(caseId))
    .map(([caseId, payload]) => ({ caseId, payload }));
}

export async function markHostedWaitResumed(
  store: Pick<EngineStore, "appendDomainEvent">,
  caseId: string,
  at: string,
): Promise<void> {
  await store.appendDomainEvent(RESUMED, at, { caseId });
}
