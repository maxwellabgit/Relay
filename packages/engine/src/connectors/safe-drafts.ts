import type { FoundationStore } from "../cases/foundation-store.js";
import { isSafeDraftAction, type SafeDraftAction } from "./provider-boundary.js";

export type SafeDraftPort = {
  create(action: SafeDraftAction, title: string, body: string): Promise<{ providerReceiptId: string }>;
};

export type SafeDraftRecord = {
  readonly draftId: string;
  readonly action: SafeDraftAction;
  readonly title: string;
  readonly bodyRef: string;
  readonly disposition: "pending" | "created" | "dismissed";
  readonly providerReceiptId: string | null;
};

export class SafeDrafts {
  constructor(
    private readonly records: FoundationStore,
    private readonly ids: { next(prefix: string): string },
    private readonly now: () => string,
    private readonly port: SafeDraftPort | null,
    private readonly putBody: (body: string) => Promise<string>,
  ) {}

  async propose(action: string, title: string, body: string): Promise<{ ok: true; draftId: string } | { ok: false; reason: string }> {
    if (!isSafeDraftAction(action)) return { ok: false, reason: "unsafe_provider_action" };
    const draftId = this.ids.next("draft");
    const bodyRef = await this.putBody(body);
    const record: SafeDraftRecord = {
      draftId,
      action,
      title,
      bodyRef,
      disposition: "pending",
      providerReceiptId: null,
    };
    await this.records.put("safe_draft", draftId, 1, record, this.now());
    return { ok: true, draftId };
  }

  async commit(draftId: string): Promise<{ ok: true; providerReceiptId: string } | { ok: false; reason: string }> {
    const row = await this.records.get("safe_draft", draftId);
    if (!row) return { ok: false, reason: "draft_missing" };
    const draft = row.payload as SafeDraftRecord;
    if (draft.disposition !== "pending") return { ok: false, reason: "draft_not_pending" };
    if (!this.port) return { ok: false, reason: "provider_unavailable" };
    const created = await this.port.create(draft.action, draft.title, draft.bodyRef);
    const next: SafeDraftRecord = { ...draft, disposition: "created", providerReceiptId: created.providerReceiptId };
    const stored = await this.records.replaceIfVersion("safe_draft", draftId, row.version, row.version + 1, next, this.now());
    if (!stored) return { ok: false, reason: "draft_conflict" };
    return { ok: true, providerReceiptId: created.providerReceiptId };
  }
}
