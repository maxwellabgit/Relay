import type { ModelChunkSink } from "./delivery.js";

export type ModelFilePort = {
  size(path: string): Promise<number | null>;
  freeBytes(directory: string): Promise<number>;
  append(path: string, chunk: Uint8Array): Promise<void>;
  rename(from: string, to: string): Promise<void>;
  remove(path: string): Promise<void>;
  hash(path: string): Promise<string>;
};

/**
 * Streams a model to a `.part` file and renames it only after the hash matches.
 * Callers never pass the whole model as one buffer.
 */
export class FileModelSink implements ModelChunkSink {
  constructor(
    private readonly port: ModelFilePort,
    private readonly directory: string,
    private readonly fileName: string,
  ) {}

  async prepare(expectedBytes: number): Promise<void> {
    const existing = (await this.port.size(this.partPath())) ?? 0;
    const free = await this.port.freeBytes(this.directory);
    if (free + existing < expectedBytes) {
      throw new Error("model_disk_full");
    }
  }

  async received(): Promise<number> {
    return (await this.port.size(this.partPath())) ?? 0;
  }

  async append(chunk: Uint8Array): Promise<void> {
    await this.port.append(this.partPath(), chunk);
  }

  async digest(): Promise<string> {
    return this.port.hash(this.partPath());
  }

  async reset(): Promise<void> {
    await this.port.remove(this.partPath());
  }

  async commit(): Promise<void> {
    await this.port.rename(this.partPath(), this.finalPath());
  }

  private partPath(): string {
    return `${this.directory}/${this.fileName}.part`;
  }

  private finalPath(): string {
    return `${this.directory}/${this.fileName}`;
  }
}
