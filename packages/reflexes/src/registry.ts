import type { ReflexModule } from "@relay/contracts";
import { resolveAcronymV1, createResolveAcronymModule } from "./resolve-acronym/handler.js";
import { createStoreGlossaryLookup, type GlossaryMemoryPort } from "./resolve-acronym/glossary-lookup.js";

export function createProductionReflexes(learning: GlossaryMemoryPort): readonly ReflexModule[] {
  return [
    createResolveAcronymModule({
      glossary: createStoreGlossaryLookup(learning),
    }),
  ];
}

export const productionReflexes = [resolveAcronymV1] satisfies readonly ReflexModule[];

export * from "./resolve-acronym/definition.js";
export * from "./resolve-acronym/detector.js";
export * from "./resolve-acronym/handler.js";
export * from "./resolve-acronym/questions.js";
export * from "./resolve-acronym/glossary-lookup.js";
