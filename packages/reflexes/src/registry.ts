import type { ReflexModule } from "@relay/contracts";
import { resolveAcronymV1 } from "./resolve-acronym/handler.js";

export const productionReflexes = [resolveAcronymV1] satisfies readonly ReflexModule[];

export * from "./resolve-acronym/definition.js";
export * from "./resolve-acronym/detector.js";
export * from "./resolve-acronym/handler.js";
export * from "./resolve-acronym/questions.js";
