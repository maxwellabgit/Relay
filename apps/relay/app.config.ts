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
  version: "0.1.0",
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
      UIBackgroundModes: ["audio"],
      NSMicrophoneUsageDescription: "RELAY listens only while you enable Listen.",
      NSBluetoothAlwaysUsageDescription: "RELAY connects to Brilliant Halo glasses.",
      NSBluetoothPeripheralUsageDescription: "RELAY connects to Brilliant Halo glasses.",
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
    },
  },
});
