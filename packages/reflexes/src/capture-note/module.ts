import { phraseDefinition, phraseModule } from "../phrase-module.js";

const definition = phraseDefinition({
  id: "reflex.capture-note",
  displayName: "Capture note",
  trigger: "note_prefix",
  explanationTemplate: "Saved a local note from an explicit phrase.",
});

export const captureNoteModule = phraseModule(
  definition,
  /^(?:note:|save note:)\s+(.{1,280})$/i,
  (token) => `note:${token}`,
);
