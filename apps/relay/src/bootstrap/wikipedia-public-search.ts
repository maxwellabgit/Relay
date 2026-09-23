import type { PublicSearchHit, PublicSearchPort } from "@relay/contracts";

const ENDPOINT = "https://en.wikipedia.org/w/api.php";
const TIMEOUT_MS = 12_000;
const PASSAGE_LIMIT = 2_000;

type FetchLike = (input: string, init: { signal: AbortSignal }) => Promise<Response>;

/**
 * Read-only Wikipedia lookup. OpenSearch titles are candidates only.
 * A hit is returned after the intro passage is fetched. Suggestions are not evidence.
 * Wikipedia passages are secondary sources.
 */
export function createWikipediaPublicSearch(fetchImpl: FetchLike = fetch): PublicSearchPort {
  return {
    async search(query, signal) {
      const trimmed = query.trim();
      if (!trimmed) return [];
      const suggestions = await readJson(fetchImpl, openSearchUrl(trimmed), signal);
      if (signal.aborted) throw new DOMException("aborted", "AbortError");
      const candidates = parseOpenSearch(suggestions);
      const hits: PublicSearchHit[] = [];
      for (const candidate of candidates) {
        if (signal.aborted) throw new DOMException("aborted", "AbortError");
        const passage = extractPassage(await readJson(fetchImpl, extractUrl(candidate.title), signal));
        if (!passage) continue;
        hits.push({
          title: candidate.title,
          url: candidate.url,
          snippet: passage,
          retrievedAt: new Date().toISOString(),
          role: "secondary",
        });
      }
      return hits;
    },
  };
}

function openSearchUrl(query: string): string {
  const url = new URL(ENDPOINT);
  url.searchParams.set("action", "opensearch");
  url.searchParams.set("search", query.slice(0, 240));
  url.searchParams.set("limit", "5");
  url.searchParams.set("namespace", "0");
  url.searchParams.set("format", "json");
  url.searchParams.set("origin", "*");
  return url.toString();
}

function extractUrl(title: string): string {
  const url = new URL(ENDPOINT);
  url.searchParams.set("action", "query");
  url.searchParams.set("prop", "extracts");
  url.searchParams.set("explaintext", "1");
  url.searchParams.set("exintro", "1");
  url.searchParams.set("redirects", "1");
  url.searchParams.set("titles", title);
  url.searchParams.set("format", "json");
  url.searchParams.set("origin", "*");
  return url.toString();
}

async function readJson(fetchImpl: FetchLike, url: string, signal: AbortSignal): Promise<unknown> {
  const timeout = AbortSignal.timeout(TIMEOUT_MS);
  const combined = AbortSignal.any([signal, timeout]);
  let response: Response;
  try {
    response = await fetchImpl(url, { signal: combined });
  } catch (error) {
    if (signal.aborted) throw error;
    return null;
  }
  if (signal.aborted) throw new DOMException("aborted", "AbortError");
  if (!response.ok) return null;
  return response.json() as Promise<unknown>;
}

function parseOpenSearch(body: unknown): { title: string; url: string }[] {
  if (!Array.isArray(body) || body.length < 4) return [];
  const titles = Array.isArray(body[1]) ? body[1] : [];
  const urls = Array.isArray(body[3]) ? body[3] : [];
  const candidates: { title: string; url: string }[] = [];
  for (let index = 0; index < titles.length && candidates.length < 5; index += 1) {
    const title = typeof titles[index] === "string" ? titles[index] : "";
    const url = typeof urls[index] === "string" ? urls[index] : "";
    if (!title || !url.startsWith("https://")) continue;
    candidates.push({ title, url });
  }
  return candidates;
}

function extractPassage(body: unknown): string {
  if (!body || typeof body !== "object") return "";
  const query = (body as { query?: { pages?: unknown } }).query;
  const pages = query?.pages;
  if (!pages || typeof pages !== "object") return "";
  for (const page of Object.values(pages as Record<string, unknown>)) {
    if (!page || typeof page !== "object") continue;
    const record = page as { extract?: unknown; missing?: unknown };
    if (record.missing != null) continue;
    if (typeof record.extract !== "string") continue;
    const text = record.extract.trim();
    if (!text) continue;
    return text.slice(0, PASSAGE_LIMIT);
  }
  return "";
}
