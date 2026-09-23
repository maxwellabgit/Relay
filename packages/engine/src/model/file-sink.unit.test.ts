import { describe, expect, it } from "vitest";
import { ModelDelivery, type ModelByteSource } from "./delivery.js";
import { FileModelSink, type ModelFilePort } from "./file-sink.js";

function memoryPort(freeBytes: number): ModelFilePort & { files: Map<string, Uint8Array[]> } {
  const files = new Map<string, Uint8Array[]>();
  return {
    files,
    async size(path) {
      const chunks = files.get(path);
      if (!chunks) return null;
      return chunks.reduce((total, chunk) => total + chunk.byteLength, 0);
    },
    async freeBytes() {
      return freeBytes;
    },
    async append(path, chunk) {
      const chunks = files.get(path) ?? [];
      chunks.push(chunk.slice());
      files.set(path, chunks);
    },
    async rename(from, to) {
      const chunks = files.get(from);
      if (!chunks) throw new Error("missing");
      files.set(to, chunks);
      files.delete(from);
    },
    async remove(path) {
      files.delete(path);
    },
    async hash(path) {
      const chunks = files.get(path) ?? [];
      const merged = new Uint8Array(chunks.reduce((total, chunk) => total + chunk.byteLength, 0));
      let offset = 0;
      for (const chunk of chunks) {
        merged.set(chunk, offset);
        offset += chunk.byteLength;
      }
      const digest = await crypto.subtle.digest("SHA-256", merged);
      return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
    },
  };
}

describe("file model sink", () => {
  it("rejects a large pin for free space before reading bytes", async () => {
    const port = memoryPort(10);
    const sink = new FileModelSink(port, "models", "candidate.bin");
    const calls = { count: 0 };
    const source: ModelByteSource = {
      async read() {
        calls.count += 1;
        return new Uint8Array();
      },
    };
    const delivery = new ModelDelivery(
      {
        id: "large",
        version: "1",
        license: "example",
        byteSize: 33 * 1024 * 1024,
        sha256: "ab".repeat(32),
      },
      source,
      4,
      sink,
    );
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("rejected");
    expect(view.message).toBe("There is not enough free space for this model.");
    expect(calls.count).toBe(0);
    expect(port.files.size).toBe(0);
  });

  it("resumes a partial file and commits it by rename", async () => {
    const port = memoryPort(1024 * 1024);
    const sink = new FileModelSink(port, "models", "candidate.bin");
    const payload = new TextEncoder().encode("abcdefghij");
    await port.append("models/candidate.bin.part", payload.subarray(0, 4));
    const source: ModelByteSource = {
      async read(offset, length) {
        return payload.subarray(offset, offset + length);
      },
    };
    const digest = await crypto.subtle.digest("SHA-256", payload);
    const sha256 = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
    const delivery = new ModelDelivery(
      { id: "small", version: "1", license: "example", byteSize: payload.byteLength, sha256 },
      source,
      3,
      sink,
    );
    const view = await delivery.start(new AbortController().signal);
    expect(view.phase).toBe("installed");
    expect(view.bytesReceived).toBe(payload.byteLength);
    expect(port.files.has("models/candidate.bin.part")).toBe(false);
    expect(port.files.has("models/candidate.bin")).toBe(true);
    const stored = port.files.get("models/candidate.bin") ?? [];
    expect(stored.length).toBeGreaterThan(1);
    expect(stored.some((chunk) => chunk.byteLength === payload.byteLength)).toBe(false);
  });
});
