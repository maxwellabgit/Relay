import type { ArtifactRef, ArtifactStorePort, DataPolicy } from "@relay/contracts";

export type SecretStore = {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
};

export type ByteFilePort = {
  write(name: string, bytes: Uint8Array): Promise<void>;
  read(name: string): Promise<Uint8Array | null>;
};

const KEY_ID = "artifact_data_key";

/**
 * Content-addressed artifacts encrypted with an app data key.
 * SQLite stores only artifact ids and hashes — never plaintext prose.
 */
export class EncryptedArtifactStore implements ArtifactStorePort {
  constructor(
    private readonly files: ByteFilePort,
    private readonly secrets: SecretStore,
  ) {}

  async put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef> {
    const sha256 = await sha256Hex(value);
    const artifactId = `artifact_${sha256.slice(0, 24)}`;
    const existing = await this.files.read(fileName(artifactId));
    if (!existing) {
      const key = await this.dataKey();
      const iv = crypto.getRandomValues(new Uint8Array(12));
      const cipher = new Uint8Array(
        await crypto.subtle.encrypt({ name: "AES-GCM", iv: asBuffer(iv) }, key, asBuffer(value)),
      );
      const packed = new Uint8Array(iv.length + cipher.length);
      packed.set(iv, 0);
      packed.set(cipher, iv.length);
      await this.files.write(fileName(artifactId), packed);
    }
    return { artifactId, sha256, policy };
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    const packed = await this.files.read(fileName(ref.artifactId));
    if (!packed) throw new Error(`artifact_missing:${ref.artifactId}`);
    if (packed.length < 13) throw new Error("artifact_corrupt");
    const iv = packed.slice(0, 12);
    const cipher = packed.slice(12);
    const key = await this.dataKey();
    const plain = new Uint8Array(
      await crypto.subtle.decrypt({ name: "AES-GCM", iv: asBuffer(iv) }, key, asBuffer(cipher)),
    );
    const actual = await sha256Hex(plain);
    if (actual !== ref.sha256) throw new Error("artifact_hash_mismatch");
    return plain;
  }

  private async dataKey(): Promise<CryptoKey> {
    let encoded = await this.secrets.get(KEY_ID);
    if (!encoded) {
      const raw = crypto.getRandomValues(new Uint8Array(32));
      encoded = bytesToB64(raw);
      await this.secrets.set(KEY_ID, encoded);
    }
    const raw = b64ToBytes(encoded);
    return crypto.subtle.importKey("raw", asBuffer(raw), "AES-GCM", false, ["encrypt", "decrypt"]);
  }
}

export class MemoryByteFiles implements ByteFilePort {
  private readonly files = new Map<string, Uint8Array>();

  async write(name: string, bytes: Uint8Array): Promise<void> {
    this.files.set(name, bytes.slice());
  }

  async read(name: string): Promise<Uint8Array | null> {
    const value = this.files.get(name);
    return value ? value.slice() : null;
  }
}

export class MemorySecretStore implements SecretStore {
  private readonly values = new Map<string, string>();

  async get(key: string): Promise<string | null> {
    return this.values.get(key) ?? null;
  }

  async set(key: string, value: string): Promise<void> {
    this.values.set(key, value);
  }

  async delete(key: string): Promise<void> {
    this.values.delete(key);
  }
}

function fileName(artifactId: string): string {
  return `${artifactId}.bin`;
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", asBuffer(bytes));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function asBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

function bytesToB64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function b64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
