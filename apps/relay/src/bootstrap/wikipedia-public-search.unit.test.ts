import { describe, expect, it } from "vitest";
import { createWikipediaPublicSearch } from "./wikipedia-public-search.js";

describe("wikipedia public search", () => {
  it("returns citations and ignores non-https rows", async () => {
    const search = createWikipediaPublicSearch(async () =>
      Response.json(["API", ["API"], ["An interface"], ["https://en.wikipedia.org/wiki/API"]]),
    );
    const hits = await search.search("API", new AbortController().signal);
    expect(hits).toEqual([
      expect.objectContaining({
        title: "API",
        url: "https://en.wikipedia.org/wiki/API",
        snippet: "An interface",
      }),
    ]);
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
