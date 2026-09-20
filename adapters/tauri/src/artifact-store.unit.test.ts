import { describe, expect, it, vi } from "vitest";
import { localOnlyPolicy } from "@relay/contracts";
import { TauriArtifactStore } from "./artifact-store.js";

describe("TauriArtifactStore", () => {
  it("puts and gets through invoke with content addressing", async () => {
    const blobs = new Map<string, { sha256: string; b64: string }>();
    const invoke = vi.fn(async (command: string, args?: Record<string, unknown>) => {
      if (command === "artifact_put") {
        const request = (args as { request: { bytes_b64: string } }).request;
        const bytes = Uint8Array.from(atob(request.bytes_b64), (c) => c.charCodeAt(0));
        const digest = await sha256Hex(bytes);
        const artifactId = `artifact_${digest.slice(0, 24)}`;
        blobs.set(artifactId, { sha256: digest, b64: request.bytes_b64 });
        return { artifact_id: artifactId, sha256: digest };
      }
      if (command === "artifact_get") {
        const request = (args as { request: { artifact_id: string; sha256: string } }).request;
        const blob = blobs.get(request.artifact_id);
        if (!blob) throw new Error("artifact_missing");
        if (blob.sha256 !== request.sha256) throw new Error("artifact_hash_mismatch");
        return { bytes_b64: blob.b64 };
      }
      throw new Error(`unexpected:${command}`);
    });

    const store = new TauriArtifactStore(invoke);
    const payload = new TextEncoder().encode("relay-sentinel-artifact-v1");
    const ref = await store.put(payload, localOnlyPolicy());
    const again = await store.put(payload, localOnlyPolicy());
    expect(again.artifactId).toBe(ref.artifactId);
    expect(again.sha256).toBe(ref.sha256);

    const roundtrip = await store.get(ref);
    expect(new TextDecoder().decode(roundtrip)).toBe("relay-sentinel-artifact-v1");
  });

  it("rejects missing and hash-mismatched artifacts", async () => {
    const invoke = vi.fn(async (command: string) => {
      if (command === "artifact_get") throw new Error("artifact_missing");
      throw new Error(`unexpected:${command}`);
    });
    const store = new TauriArtifactStore(invoke);
    await expect(
      store.get({ artifactId: "missing", sha256: "abc", policy: localOnlyPolicy() }),
    ).rejects.toThrow(/artifact_missing/);

    const mismatch = vi.fn(async () => {
      throw new Error("artifact_hash_mismatch");
    });
    const store2 = new TauriArtifactStore(mismatch);
    await expect(
      store2.get({ artifactId: "x", sha256: "bad", policy: localOnlyPolicy() }),
    ).rejects.toThrow("artifact_hash_mismatch");
  });
});

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const copy = new Uint8Array(bytes);
  const digest = await crypto.subtle.digest("SHA-256", copy);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}
