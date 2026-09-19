/** Expo/iOS adapter — SecureStore secrets and expo-sqlite land with TestFlight builds. */

export type ExpoSecretStore = {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
};

export type ExpoSystemOneTransport = {
  systemOne(request: {
    model: string;
    payload: unknown;
  }): Promise<
    | { ok: true; body: unknown }
    | { ok: false; category: "disabled" | "missing_secret" | "network"; message: string }
  >;
};

/**
 * Missing key means disabled — never a fake positive, never compile secrets into JS.
 */
export function createExpoSystemOneTransport(secrets: ExpoSecretStore): ExpoSystemOneTransport {
  return {
    async systemOne() {
      const key = await secrets.get("typesafe_api_key");
      if (!key) {
        return { ok: false, category: "missing_secret", message: "typesafe_key_missing" };
      }
      return { ok: false, category: "disabled", message: "native_system_one_pending_dev_client" };
    },
  };
}

export const EXPO_ADAPTER_BOOTSTRAP = "0.1.0" as const;
