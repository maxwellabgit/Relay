import type { EventEnvelope } from "@relay/contracts";

/** Read-only pull. A missing port means the account is not connected. */
export type SourceReadPort = {
  pull(resourceId: string): Promise<readonly EventEnvelope[]>;
};

export const SAFE_DRAFT_ACTIONS = [
  "gmail.draft.create",
  "sheets.spreadsheet.create",
  "docs.document.create",
] as const;

export type SafeDraftAction = (typeof SAFE_DRAFT_ACTIONS)[number];

export function isSafeDraftAction(action: string): action is SafeDraftAction {
  return (SAFE_DRAFT_ACTIONS as readonly string[]).includes(action);
}

/** New drafts and new files only. Send, delete, and edits of existing files are refused. */
export function unsafeProviderAction(action: string): boolean {
  return !isSafeDraftAction(action);
}
