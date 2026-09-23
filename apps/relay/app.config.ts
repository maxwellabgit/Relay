import type { ConfigContext, ExpoConfig } from "expo/config";

/**
 * RELAY iPhone / web app config.
 * Do not copy NepTranslate bundle IDs, EAS project IDs, or secrets.
 * TypeSafe keys must never appear here or in JS assets.
 */
export default ({ config }: ConfigContext): ExpoConfig => ({
  ...config,
  name: "RELAY",
  slug: "relay-assistant",
  version: "1.0.0",
  orientation: "portrait",
  scheme: "relay",
  userInterfaceStyle: "automatic",
  web: {
    bundler: "metro",
    output: "single",
  },
  ios: {
    supportsTablet: false,
    bundleIdentifier: "app.relay.assistant",
    infoPlist: {
      // Foreground listening only. Halo is not a V1 capability, so Bluetooth is not declared.
      NSMicrophoneUsageDescription: "RELAY listens only while you enable Listen.",
      NSSpeechRecognitionUsageDescription: "RELAY transcribes speech only while Listen is on.",
    },
  },
  android: {
    package: "app.relay.assistant",
  },
  plugins: [],
  extra: {
    eas: {
      // Create a new EAS project for RELAY — do not reuse NepTranslate's projectId.
      projectId: "00000000-0000-0000-0000-000000000000",
    },
    relay: {
      protocolVersion: "1",
      requiresDevClient: true,
      marketingVersion: "1.0.0",
      applicationId: "app.relay.assistant",
    },
  },
});
