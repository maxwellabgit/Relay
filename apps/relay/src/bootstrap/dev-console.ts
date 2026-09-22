/**
 * Developer UI is allowed only on the internal channel, and only when the
 * console flag is also set. A production channel, or a missing channel,
 * stays hidden even if the flag is set.
 */
export function developerConsoleAllowed(
  env: Record<string, string | undefined> | undefined,
): boolean {
  if (!env || env.EXPO_PUBLIC_RELAY_CHANNEL !== "internal") return false;
  const flag = env.EXPO_PUBLIC_RELAY_DEV_CONSOLE;
  return flag === "1" || flag === "true";
}

export function readProcessEnv(): Record<string, string | undefined> {
  return (
    (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env ?? {}
  );
}
