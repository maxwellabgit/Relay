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

export async function secretImportStagingFile(): Promise<void> {
  const { invoke } = await import("@tauri-apps/api/core");
  await invoke("secret_import_staging_file");
}

export async function secretDelete(name: string): Promise<void> {
  const { invoke } = await import("@tauri-apps/api/core");
  await invoke("secret_delete", { request: { name } });
}

export async function systemOne(
  request: HostedSystemOneRequest,
): Promise<HostedSystemOneResponse> {
  const { invoke } = await import("@tauri-apps/api/core");
  const result = await invoke<{
    ok: boolean;
    status: number;
    category: string;
    latency_ms: number;
    retries: number;
    body: unknown | null;
  }>("typesafe_judge", { request: { model: request.model, body: request.payload } });
  if (!result.ok) return { ok: false, category: result.category, message: result.category };
  return { ok: true, body: result.body };
}

export async function openRunFolder(): Promise<string> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<string>("open_run_folder");
}

export async function haloStatus(): Promise<string> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<string>("halo_status");
}
