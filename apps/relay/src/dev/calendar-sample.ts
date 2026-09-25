/**
 * Developer-console sample. Production Metro redirects this module away
 * unless the internal dev-console flag is on. It is not a live connector.
 */
export async function injectBirthdaySample(engine: { installCalendarFixture(): Promise<void> }): Promise<void> {
  await engine.installCalendarFixture();
}
