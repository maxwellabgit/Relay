import { describe, expect, it } from "vitest";
import { nextThreadScroll, threadRestoreTarget } from "./thread-scroll.js";

describe("thread scroll", () => {
  it("follows the end only when the reader is already near it", () => {
    expect(
      nextThreadScroll({ contentHeight: 1000, viewportHeight: 400, offsetY: 560 }),
    ).toEqual({ pinned: true, offset: 560 });
    expect(
      nextThreadScroll({ contentHeight: 1000, viewportHeight: 400, offsetY: 400 }),
    ).toEqual({ pinned: false, offset: 400 });
  });

  it("drops a non-numeric offset and still reports follow state", () => {
    expect(
      nextThreadScroll({ contentHeight: 200, viewportHeight: 100, offsetY: Number.NaN }),
    ).toEqual({ pinned: false, offset: 0 });
  });

  it("restores the end when pinned and the saved offset when the reader moved up", () => {
    expect(threadRestoreTarget({ pinned: true, offset: 40 })).toEqual({ kind: "end" });
    expect(threadRestoreTarget({ pinned: false, offset: 320 })).toEqual({
      kind: "offset",
      offset: 320,
    });
  });
});
