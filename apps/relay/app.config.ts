import type { ConfigContext, ExpoConfig } from "expo/config";

export default ({ config }: ConfigContext): ExpoConfig => ({
  ...config,
  name: "RELAY",
  slug: "relay",
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
  },
  android: {
    package: "app.relay.assistant",
  },
  extra: {
    eas: {
      projectId: "00000000-0000-0000-0000-000000000000",
    },
  },
});
