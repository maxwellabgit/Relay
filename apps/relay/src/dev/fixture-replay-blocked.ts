/** Production bundle target. Fixture events live in the dev-only module. */
export async function replayAcronymFixture(): Promise<void> {
  throw new Error("dev_console_disabled");
}
