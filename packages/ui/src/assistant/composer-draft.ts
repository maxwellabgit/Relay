/** Keep a draft when a send is refused. Clear it only after that same text is accepted. */

export function canSendComposer(input: {
  readonly text: string;
  readonly disabled: boolean;
  readonly busy: boolean;
  readonly sending: boolean;
}): boolean {
  return input.text.trim().length > 0 && !input.disabled && !input.busy && !input.sending;
}

export function nextComposerDraft(current: string, submitted: string, accepted: boolean): string {
  if (!accepted) return current;
  return current.trim() === submitted ? "" : current;
}
