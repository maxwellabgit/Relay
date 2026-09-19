/** Engine-side policies that are not Reflex-specific. */

export function directAskPriority(): number {
  return 100;
}

export function observedCasePriority(): number {
  return 50;
}

export function shouldCreateCaseForFinal(origin: string, isAsk: boolean): {
  origin: "direct" | "observed";
  kind: "answer" | "resolve";
  priority: number;
} {
  if (isAsk) {
    return { origin: "direct", kind: "answer", priority: directAskPriority() };
  }
  return { origin: "observed", kind: "resolve", priority: observedCasePriority() };
}
