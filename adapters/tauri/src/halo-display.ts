import type { GlassesDisplayPort, GlanceFrame } from "@relay/contracts";

/**
 * Tauri-side Halo display port.
 * Spawns the allowlisted Python bridge as a sidecar in later wiring;
 * for Stage 2 tests it records frames in-process.
 */
export class HaloDisplayPort implements GlassesDisplayPort {
  readonly frames: GlanceFrame[] = [];
  private connected = true;

  async show(frame: GlanceFrame): Promise<void> {
    if (!this.connected) {
      throw new Error("halo_disconnected");
    }
    // Never send decision logs or full transcripts — brief UI only.
    const safe: GlanceFrame = {
      kind: frame.kind,
      ...(frame.title ? { title: frame.title.slice(0, 40) } : {}),
      ...(frame.body ? { body: frame.body.slice(0, 192) } : {}),
      ...(frame.truncated != null ? { truncated: frame.truncated } : {}),
    };
    this.frames.push(safe);
  }

  async clear(): Promise<void> {
    this.frames.push({ kind: "clear" });
  }

  disconnect(): void {
    this.connected = false;
  }

  reconnect(): void {
    this.connected = true;
  }
}

export function frameFromFinding(title: string, body: string): GlanceFrame {
  return { kind: "finding", title, body };
}
