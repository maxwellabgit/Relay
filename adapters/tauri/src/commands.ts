/** Typed Tauri invoke wrappers — no generic shell/filesystem exposure. */

export type HostedSystemOneRequest = {
  readonly model: string;
  readonly payload: unknown;
};

export type HostedSystemOneResponse =
  | { readonly ok: true; readonly body: unknown }
  | { readonly ok: false; readonly category: string; readonly message: string };

export async function ping(): Promise<string> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<string>("ping");
}

export async function secretStatus(): Promise<string> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<string>("secret_status");
}

export async function systemOne(
  request: HostedSystemOneRequest,
): Promise<HostedSystemOneResponse> {
  void request;
  // Native transport lands with secret storage; missing key → disabled.
  return { ok: false, category: "disabled", message: "native_system_one_pending" };
}

export async function haloStatus(): Promise<string> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<string>("halo_status");
}
