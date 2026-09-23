import { describe, expect, it } from "vitest";
import { createWikipediaPublicSearch } from "./wikipedia-public-search.js";

const passage = "An application programming interface is a way for programs to talk to each other.";

describe("wikipedia public search", () => {
  it("returns the cited passage and ignores OpenSearch suggestions", async () => {
    const search = createWikipediaPublicSearch(async (url) => {
      if (url.includes("action=opensearch")) {
        return Response.json([
          "API",
          ["API", "Skipped"],
          ["An interface", "not a passage"],
          ["https://en.wikipedia.org/wiki/API", "http://insecure.example/skipped"],
        ]);
      }
      return Response.json({
        query: { pages: { "1": { title: "API", extract: passage } } },
      });
    });
    const hits = await search.search("API", new AbortController().signal);
    expect(hits).toEqual([
      expect.objectContaining({
        title: "API",
        url: "https://en.wikipedia.org/wiki/API",
        snippet: passage,
        role: "secondary",
      }),
    ]);
    expect(hits[0]?.snippet).not.toBe("An interface");
  });

  it("returns no hits when only search suggestions are available", async () => {
    const search = createWikipediaPublicSearch(async (url) => {
      if (url.includes("action=opensearch")) {
        return Response.json(["API", ["API"], ["An interface"], ["https://en.wikipedia.org/wiki/API"]]);
      }
      return Response.json({ query: { pages: { "1": { missing: "" } } } });
    });
    await expect(search.search("API", new AbortController().signal)).resolves.toEqual([]);
  });

  it("returns no hits when the network fails", async () => {
    const search = createWikipediaPublicSearch(async () => {
      throw new Error("offline");
    });
    await expect(search.search("API", new AbortController().signal)).resolves.toEqual([]);
  });

  it("returns no hits for an empty body", async () => {
    const search = createWikipediaPublicSearch(async () => Response.json(["q", [], [], []]));
    await expect(search.search("none", new AbortController().signal)).resolves.toEqual([]);
  });

  it("stops when the caller cancels", async () => {
    const controller = new AbortController();
    controller.abort();
    const search = createWikipediaPublicSearch(async (_url, init) => {
      if (init.signal.aborted) throw new DOMException("aborted", "AbortError");
      return Response.json([]);
    });
    await expect(search.search("API", controller.signal)).rejects.toThrow(/abort/i);
  });
});
