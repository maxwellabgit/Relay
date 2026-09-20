import type { GlossaryLookup } from "./handler.js";
import dictionary from "./data/technical-acronyms.v1.json" with { type: "json" };

type DictionaryFile = {
  readonly schemaVersion: number;
  readonly entries: Readonly<Record<string, string>>;
};

export type GlossaryMemoryPort = {
  getMemory(
    kind: "glossary",
    key: string,
  ): Promise<{
    readonly source: string;
    readonly value: { readonly expansion?: string };
  } | null>;
};

const bundled = (dictionary as DictionaryFile).entries;

export function bundledDictionaryLookup(token: string): string | null {
  return bundled[token] ?? bundled[token.toUpperCase()] ?? null;
}

/**
 * Lookup precedence:
 * 1. explicit user memory
 * 2. project/local glossary (exactProject)
 * 3. bundled exact dictionary
 * 4. context-derived candidates (searchWindow)
 */
export function createStoreGlossaryLookup(
  learning: GlossaryMemoryPort,
  extras: {
    readonly exactProject?: (token: string) => string | null | Promise<string | null>;
    readonly searchContext?: (token: string, text: string) => string[] | Promise<string[]>;
  } = {},
): GlossaryLookup {
  return {
    async exactUser(token) {
      const memory = await learning.getMemory("glossary", token);
      if (!memory || memory.source !== "explicit_user") return null;
      const expansion = memory.value.expansion;
      return typeof expansion === "string" && expansion.trim() ? expansion.trim() : null;
    },
    async exactProject(token) {
      if (!extras.exactProject) return null;
      return (await extras.exactProject(token)) ?? null;
    },
    async exactBundled(token) {
      return bundledDictionaryLookup(token);
    },
    async searchWindow(token, text) {
      if (extras.searchContext) {
        return [...(await extras.searchContext(token, text))];
      }
      const re = new RegExp(
        `${token}\\s+(?:means|stands for|=)\\s+([A-Za-z][A-Za-z\\s-]{2,80})`,
        "i",
      );
      const m = text.match(re);
      return m?.[1] ? [m[1].trim()] : [];
    },
  };
}

export function createFixtureGlossaryLookup(map: Readonly<Record<string, string | string[]>>): GlossaryLookup {
  return {
    async exactUser() {
      return null;
    },
    async exactProject(token) {
      const value = map[token];
      return typeof value === "string" ? value : null;
    },
    async exactBundled() {
      return null;
    },
    async searchWindow(token) {
      const value = map[token];
      return Array.isArray(value) ? [...value] : [];
    },
  };
}
