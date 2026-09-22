import {
  MAX_MODEL_DOWNLOAD_BYTES,
  formatModelBytes,
  unselectedModelDelivery,
  type ModelDeliveryView,
  type ModelPin,
} from "@relay/contracts";

export type ModelByteSource = {
  read(offset: number, length: number, signal: AbortSignal): Promise<Uint8Array>;
};

const DEFAULT_CHUNK_BYTES = 262_144;

/**
 * Downloads a pinned model only when a pin and a byte source exist.
 * A missing pin never calls the source.
 */
export class ModelDelivery {
  private phase: ModelDeliveryView["phase"] = "unselected";
  private bytes = new Uint8Array(0);
  private message = unselectedModelDelivery().message;
  private generation = 0;
  private paused = false;

  constructor(
    private readonly pin: ModelPin | null,
    private readonly source?: ModelByteSource,
    private readonly chunkBytes = DEFAULT_CHUNK_BYTES,
  ) {
    if (pin) {
      this.phase = "available";
      this.message = offerMessage(pin);
    }
  }

  view(): ModelDeliveryView {
    return {
      phase: this.phase,
      pin: this.pin,
      bytesReceived: this.bytes.byteLength,
      message: this.message,
      wifiRecommended: this.pin !== null && this.phase !== "installed" && this.phase !== "unselected",
    };
  }

  async start(signal: AbortSignal): Promise<ModelDeliveryView> {
    if (!this.pin) return this.view();
    if (!pinIsDownloadable(this.pin)) {
      this.phase = "rejected";
      this.bytes = new Uint8Array(0);
      this.message = rejectionMessage(this.pin);
      return this.view();
    }
    if (!this.source) {
      this.phase = "rejected";
      this.bytes = new Uint8Array(0);
      this.message = "No download source is configured.";
      return this.view();
    }
    if (this.phase === "installed") return this.view();
    if (signal.aborted) {
      this.phase = "paused";
      this.message = "Download paused.";
      return this.view();
    }
    this.paused = false;
    this.generation += 1;
    const generation = this.generation;
    this.phase = "downloading";
    this.message = `Downloading ${formatModelBytes(this.pin.byteSize)}. Use Wi-Fi.`;
    await this.pump(signal, generation);
    return this.view();
  }

  pause(): ModelDeliveryView {
    this.paused = true;
    if (this.phase === "downloading" || this.phase === "verifying") {
      this.phase = "paused";
      this.message = "Download paused.";
    }
    return this.view();
  }

  cancel(): ModelDeliveryView {
    this.generation += 1;
    this.paused = true;
    this.bytes = new Uint8Array(0);
    if (this.pin) {
      this.phase = "available";
      this.message = offerMessage(this.pin);
    }
    return this.view();
  }

  deleteLocal(): ModelDeliveryView {
    if (this.phase !== "installed") return this.view();
    this.generation += 1;
    this.bytes = new Uint8Array(0);
    if (!this.pin) return unselectedModelDelivery();
    this.phase = "available";
    this.message = offerMessage(this.pin);
    return this.view();
  }

  private async pump(signal: AbortSignal, generation: number): Promise<void> {
    const pin = this.pin;
    const source = this.source;
    if (!pin || !source) return;
    while (this.bytes.byteLength < pin.byteSize) {
      if (this.generation !== generation) return;
      if (signal.aborted || this.paused) {
        this.phase = "paused";
        this.message = "Download paused.";
        return;
      }
      const length = Math.min(this.chunkBytes, pin.byteSize - this.bytes.byteLength);
      let chunk: Uint8Array;
      try {
        chunk = await source.read(this.bytes.byteLength, length, signal);
      } catch {
        if (this.generation !== generation) return;
        this.phase = "paused";
        this.message = "Download paused.";
        return;
      }
      if (this.generation !== generation) return;
      if (chunk.byteLength !== length) {
        this.bytes = new Uint8Array(0);
        this.phase = "rejected";
        this.message = "The download stopped before the expected size.";
        return;
      }
      const merged = new Uint8Array(this.bytes.byteLength + chunk.byteLength);
      merged.set(this.bytes);
      merged.set(chunk, this.bytes.byteLength);
      this.bytes = merged;
    }
    if (this.generation !== generation) return;
    this.phase = "verifying";
    this.message = "Checking the download hash.";
    const digest = await crypto.subtle.digest("SHA-256", this.bytes);
    if (this.generation !== generation) return;
    const actual = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
    if (actual !== pin.sha256) {
      this.bytes = new Uint8Array(0);
      this.phase = "rejected";
      this.message = "The download hash did not match.";
      return;
    }
    this.phase = "installed";
    this.message = `${pin.id} ${pin.version} verified.`;
  }
}

function offerMessage(pin: ModelPin): string {
  return `${pin.id} ${pin.version}. ${formatModelBytes(pin.byteSize)}. License: ${pin.license}. Use Wi-Fi.`;
}

function pinIsDownloadable(pin: ModelPin): boolean {
  return (
    pin.byteSize > 0 &&
    pin.byteSize <= MAX_MODEL_DOWNLOAD_BYTES &&
    /^[a-f0-9]{64}$/.test(pin.sha256) &&
    pin.id.trim().length > 0 &&
    pin.version.trim().length > 0
  );
}

function rejectionMessage(pin: ModelPin): string {
  if (pin.byteSize > MAX_MODEL_DOWNLOAD_BYTES) return "This model is larger than the 1.2 GB download limit.";
  if (pin.byteSize < 1) return "This model pin has no bytes.";
  return "This model pin is not usable.";
}
