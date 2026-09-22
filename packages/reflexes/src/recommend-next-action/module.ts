import { phraseDefinition, phraseModule } from "../phrase-module.js";

const definition = phraseDefinition({
  id: "reflex.recommend-next-action",
  displayName: "Recommend next action",
  trigger: "next_action_prefix",
  explanationTemplate: "Captured an explicit next action.",
});

export const recommendNextActionModule = phraseModule(
  definition,
  /^(?:next action:|what should i do next\??)\s*(.{0,280})$/i,
  (token) => `Next: ${token}`,
  { allowEmpty: true, emptySummary: "What should I do next?" },
);
