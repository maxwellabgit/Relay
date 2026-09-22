import { phraseDefinition, phraseModule } from "../phrase-module.js";

const definition = phraseDefinition({
  id: "reflex.remember-fact",
  displayName: "Remember fact",
  trigger: "remember_prefix",
  explanationTemplate: "Stored an explicit fact in local memory.",
});

export const rememberFactModule = phraseModule(
  definition,
  /^(?:remember that|remember:)\s+(.{1,280})$/i,
  (token) => `fact:${token}`,
);
