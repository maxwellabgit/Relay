import type { GenerationRequest, GenerationResponse, TextModelPort } from "@relay/contracts";
import type { ForegroundSpeechPort, SpeechStatus } from "./lifecycle.js";

type NativeSpeech = {
  status(): Promise<SpeechStatus>;
  start(): Promise<SpeechStatus>;
  stop(): Promise<SpeechStatus>;
};

type NativeModel = {
  status(): Promise<{ ok: boolean; detail: string; model?: string | null }>;
  generate(request: {
    taskKind: string;
    prompt: string;
    maxTokens: number;
    temperature: number;
  }): Promise<{ ok: boolean; text?: string; model?: string; elapsedMs?: number; failureReason?: string }>;
};

const missingSpeech: SpeechStatus = {
  ok: false,
  capturing: false,
  detail: "speech_module_missing",
};

/** Foreground device speech. A missing native module does not open a fake session. */
export function createDeviceForegroundSpeech(): ForegroundSpeechPort {
  return {
    async status() {
      const native = await loadNative<NativeSpeech>("RelaySpeech");
      return native ? native.status() : missingSpeech;
    },
    async start() {
      const native = await loadNative<NativeSpeech>("RelaySpeech");
      if (!native) return missingSpeech;
      return native.start();
    },
    async stop() {
      const native = await loadNative<NativeSpeech>("RelaySpeech");
      if (!native) return { ok: true, capturing: false, detail: "stopped" };
      return native.stop();
    },
  };
}

export function createDeviceTextModel(): TextModelPort {
  return {
    async generate(request: GenerationRequest, signal: AbortSignal): Promise<GenerationResponse> {
      if (signal.aborted) return { ok: false, failureReason: "cancelled" };
      const native = await loadNative<NativeModel>("RelayModel");
      if (!native) return { ok: false, failureReason: "model_unavailable" };
      try {
        const status = await native.status();
        if (!status.ok) return { ok: false, failureReason: "model_unavailable" };
        const result = await native.generate({
          taskKind: request.taskKind,
          prompt: request.prompt,
          maxTokens: request.maxTokens ?? 512,
          temperature: request.temperature ?? 0.2,
        });
        if (signal.aborted) return { ok: false, failureReason: "cancelled" };
        if (!result.ok || typeof result.text !== "string" || !result.text.trim()) {
          return { ok: false, failureReason: result.failureReason || "model_unavailable" };
        }
        return {
          ok: true,
          text: result.text.trim(),
          model: result.model ?? status.model ?? "on-device",
          elapsedMs: Number(result.elapsedMs ?? 0),
        };
      } catch {
        if (signal.aborted) return { ok: false, failureReason: "cancelled" };
        return { ok: false, failureReason: "model_unavailable" };
      }
    },
  };
}

export async function deviceModelStatus(): Promise<{ ok: boolean; detail: string; model: string | null }> {
  const native = await loadNative<NativeModel>("RelayModel");
  if (!native) return { ok: false, detail: "model_module_missing", model: null };
  try {
    const status = await native.status();
    return { ok: status.ok === true, detail: status.detail, model: status.model ?? null };
  } catch {
    return { ok: false, detail: "model_unavailable", model: null };
  }
}

async function loadNative<T>(name: string): Promise<T | null> {
  try {
    const dynamicImport = new Function("specifier", "return import(specifier)") as (
      specifier: string,
    ) => Promise<{ requireNativeModule?: (moduleName: string) => T }>;
    const core = await dynamicImport("expo-modules-core");
    return core.requireNativeModule?.(name) ?? null;
  } catch {
    return null;
  }
}
