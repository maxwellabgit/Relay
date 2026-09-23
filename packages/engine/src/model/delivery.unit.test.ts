import { describe, expect, it } from "vitest";
import { MAX_MODEL_DOWNLOAD_BYTES, UNSELECTED_MODEL_MESSAGE, deliveryActions } from "@relay/contracts";
import { ModelDelivery, type ModelByteSource, type ModelChunkSink } from "./delivery.js";

const text = new TextEncoder().encode("model-bytes");

async function sha256(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function sourceOf(bytes: Uint8Array, calls: { count: number }): ModelByteSource {
  return {
    async read(offset, length) {
      calls.count += 1;
      return bytes.subarray(offset, offset + length);
    },
  };
}

describe("model delivery", () => {
  it("does not download when no model is selected", async () => {
    const calls = { count: 0 };
    const delivery = new ModelDelivery(null, sourceOf(text, calls));
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("unselected");
    expect(view.message).toBe(UNSELECTED_MODEL_MESSAGE);
    expect(view.wifiRecommended).toBe(false);
    expect(calls.count).toBe(0);
    expect(deliveryActions(view.phase)).toEqual([]);
  });

  it("rejects a model above the download limit before reading bytes", async () => {
    const calls = { count: 0 };
    const delivery = new ModelDelivery(
      {
        id: "too-big",
        version: "0",
        license: "example",
        byteSize: MAX_MODEL_DOWNLOAD_BYTES + 1,
        sha256: "a".repeat(64),
      },
      sourceOf(text, calls),
    );
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("rejected");
    expect(view.message).toContain("1.2 GB");
    expect(calls.count).toBe(0);
  });

  it("verifies a matching hash and can delete the local copy", async () => {
    const calls = { count: 0 };
    const pin = {
      id: "fixture-model",
      version: "1",
      license: "example",
      byteSize: text.byteLength,
      sha256: await sha256(text),
    };
    const delivery = new ModelDelivery(pin, sourceOf(text, calls), 4);
    const installed = await delivery.start(new AbortController().signal);
    expect(installed.phase).toBe("installed");
    expect(installed.bytesReceived).toBe(text.byteLength);
    expect(installed.wifiRecommended).toBe(false);
    expect(calls.count).toBeGreaterThan(1);
    const cleared = delivery.deleteLocal();
    expect(cleared.phase).toBe("available");
    expect(cleared.bytesReceived).toBe(0);
    expect(cleared.wifiRecommended).toBe(true);
    expect(deliveryActions(cleared.phase)).toEqual(["download"]);
  });

  it("rejects a hash mismatch and drops the bytes", async () => {
    const delivery = new ModelDelivery(
      {
        id: "fixture-model",
        version: "1",
        license: "example",
        byteSize: text.byteLength,
        sha256: "b".repeat(64),
      },
      sourceOf(text, { count: 0 }),
      4,
    );
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("rejected");
    expect(view.bytesReceived).toBe(0);
    expect(view.message).toContain("hash");
  });

  it("pauses and cancel clears a partial download", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const source: ModelByteSource = {
      async read(offset, length, signal) {
        if (offset === 0) await gate;
        if (signal.aborted) throw new Error("aborted");
        return text.subarray(offset, offset + length);
      },
    };
    const pin = {
      id: "fixture-model",
      version: "1",
      license: "example",
      byteSize: text.byteLength,
      sha256: await sha256(text),
    };
    const controller = new AbortController();
    const delivery = new ModelDelivery(pin, source, 4);
    const pending = delivery.start(controller.signal);
    delivery.pause();
    controller.abort();
    release();
    const paused = await pending;
    expect(paused.phase).toBe("paused");
    const cleared = delivery.cancel();
    expect(cleared.phase).toBe("available");
    expect(cleared.bytesReceived).toBe(0);
  });

  it("refuses to assemble a large model in memory when no file sink exists", async () => {
    const calls = { count: 0 };
    const delivery = new ModelDelivery(
      {
        id: "large",
        version: "1",
        license: "example",
        byteSize: 33 * 1024 * 1024,
        sha256: "b".repeat(64),
      },
      sourceOf(text, calls),
    );
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("rejected");
    expect(view.message).toContain("file");
    expect(calls.count).toBe(0);
  });

  it("streams chunks to a sink and verifies that digest", async () => {
    const chunks: Uint8Array[] = [];
    const sink: ModelChunkSink = {
      async append(chunk) {
        chunks.push(chunk.slice());
      },
      async digest() {
        const merged = new Uint8Array(chunks.reduce((total, chunk) => total + chunk.byteLength, 0));
        let offset = 0;
        for (const chunk of chunks) {
          merged.set(chunk, offset);
          offset += chunk.byteLength;
        }
        return sha256(merged);
      },
      async reset() {
        chunks.length = 0;
      },
      async commit() {
        return undefined;
      },
    };
    const pin = {
      id: "streamed",
      version: "1",
      license: "example",
      byteSize: text.byteLength,
      sha256: await sha256(text),
    };
    const delivery = new ModelDelivery(pin, sourceOf(text, { count: 0 }), 4, sink);
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("installed");
    expect(view.bytesReceived).toBe(text.byteLength);
    expect(chunks.length).toBeGreaterThan(1);
  });
});
