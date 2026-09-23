import type { PublicSearchHit, PublicSearchPort } from "@relay/contracts";

const ENDPOINT = "https://en.wikipedia.org/w/api.php";
const TIMEOUT_MS = 12_000;

type FetchLike = (input: string, init: { signal: AbortSignal }) => Promise<Response>;

/**
 * Read-only public search. No account, no write, and no query logging.
 * Cancellation and timeout abort the request. Network failure returns no hits.
 */
export function createWikipediaPublicSearch(fetchImpl: FetchLike = fetch): PublicSearchPort {
  return {
    async search(query, signal) {
      const trimmed = query.trim();
      if (!trimmed) return [];
      const url = new URL(ENDPOINT);
      url.searchParams.set("action", "opensearch");
      url.searchParams.set("search", trimmed.slice(0, 240));
      url.searchParams.set("limit", "5");
      url.searchParams.set("namespace", "0");
      url.searchParams.set("format", "json");
      url.searchParams.set("origin", "*");
      const timeout = AbortSignal.timeout(TIMEOUT_MS);
      const combined = AbortSignal.any([signal, timeout]);
      let response: Response;
      try {
        response = await fetchImpl(url.toString(), { signal: combined });
      } catch (error) {
        if (signal.aborted) throw error;
        return [];
      }
      if (signal.aborted) throw new DOMException("aborted", "AbortError");
      if (!response.ok) return [];
      const body = (await response.json()) as unknown;
      return parseOpenSearch(body);
    },
  };
}

function parseOpenSearch(body: unknown): PublicSearchHit[] {
  if (!Array.isArray(body) || body.length < 4) return [];
  const titles = Array.isArray(body[1]) ? body[1] : [];
  const snippets = Array.isArray(body[2]) ? body[2] : [];
  const urls = Array.isArray(body[3]) ? body[3] : [];
  const retrievedAt = new Date().toISOString();
  const hits: PublicSearchHit[] = [];
  for (let index = 0; index < titles.length && hits.length < 5; index += 1) {
    const title = typeof titles[index] === "string" ? titles[index] : "";
    const url = typeof urls[index] === "string" ? urls[index] : "";
    const snippet = typeof snippets[index] === "string" ? snippets[index] : "";
    if (!title || !url.startsWith("https://")) continue;
    hits.push({ title, url, snippet: snippet.slice(0, 280), retrievedAt });
  }
  return hits;
}
