import { describe, expect, it } from "vitest";
import { developerConsoleAllowed } from "./dev-console.js";

describe("developer console channel", () => {
  it("stays hidden in production even when the flag is set", () => {
    expect(
      developerConsoleAllowed({
        EXPO_PUBLIC_RELAY_CHANNEL: "production",
        EXPO_PUBLIC_RELAY_DEV_CONSOLE: "1",
      }),
    ).toBe(false);
    expect(developerConsoleAllowed({ EXPO_PUBLIC_RELAY_DEV_CONSOLE: "1" })).toBe(false);
    expect(developerConsoleAllowed(undefined)).toBe(false);
  });

  it("opens only for the internal channel with the flag", () => {
    expect(
      developerConsoleAllowed({
        EXPO_PUBLIC_RELAY_CHANNEL: "internal",
        EXPO_PUBLIC_RELAY_DEV_CONSOLE: "true",
      }),
    ).toBe(true);
    expect(
      developerConsoleAllowed({
        EXPO_PUBLIC_RELAY_CHANNEL: "internal",
        EXPO_PUBLIC_RELAY_DEV_CONSOLE: "0",
      }),
    ).toBe(false);
  });
});