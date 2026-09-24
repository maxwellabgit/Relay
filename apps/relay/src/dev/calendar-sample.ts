import type { EventEnvelope, RelayClient } from "@relay/contracts";
import { calendarEnvelope } from "@relay/engine";

const RESOURCE = "calendar:birthdays";

/**
 * Developer-console sample. Production Metro redirects this module away
 * unless the internal dev-console flag is on. It is not a live connector.
 */
export async function injectBirthdaySample(client: Pick<RelayClient, "execute">): Promise<void> {
  const at = new Date().toISOString();
  await client.execute({
    type: "BindObservation",
    binding: {
      bindingId: "bind_birthdays",
      connectionId: "connection_calendar_sample",
      resourceId: RESOURCE,
      projectCaseIds: ["case_birthdays"],
      eventKinds: ["calendar.event"],
      contentLevel: "excerpt",
      retention: "case_entry",
      enabled: true,
      revoked: false,
      lastSyncAt: null,
      lagMs: null,
    },
  });
  await client.execute({
    type: "SetScopedGrant",
    grant: {
      grantId: "grant_birthday_notice",
      reflexId: "reflex.rule-notice",
      reflexVersion: 1,
      connectionId: "connection_calendar_sample",
      resourceIds: [RESOURCE],
      actionId: "case.entry.append@1",
      expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
      maxPerHour: 4,
    },
  });
  await client.execute({
    type: "IngestObservedEvent",
    envelope: sampleEnvelope("evt_birthday_1", "1", "Birthday: Maya 03-14", at),
  });
  await client.execute({
    type: "IngestObservedEvent",
    envelope: sampleEnvelope("evt_birthday_1", "2", "Birthday: Maya 04-01", at),
  });
}

function sampleEnvelope(externalEventId: string, revision: string, content: string, at: string): EventEnvelope {
  return calendarEnvelope({
    eventId: `event_${externalEventId}_${revision}`,
    externalEventId,
    revision,
    resourceId: RESOURCE,
    content,
    selected: true,
    at,
  });
}
