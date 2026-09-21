import type { ActionCard } from "@relay/contracts";
import type { Clock } from "../scheduler.js";
import type { EngineStore } from "../store.js";

const OVERLAY_SCAN = 5000;

const ACTION_STAGED = "overlay.action_staged";
const ACTION_CLEARED = "overlay.action_cleared";
const INPUT_PREVIEW = "overlay.input_preview";
const INPUT_PREVIEW_CLEARED = "overlay.input_preview_cleared";

type StagedActionRef =
  | {
      readonly actionId: string;
      readonly kind: "confirm_birthday";
      readonly label: string;
      readonly personId: string;
      readonly displayName: string;
      readonly date: string;
    }
  | {
      readonly actionId: string;
      readonly kind: "save_definition";
      readonly label: string;
      readonly token: string;
      /** Artifact ref for expansion prose — never store expansion text in domain events. */
      readonly expansionArtifactId: string;
      readonly expansionSha256: string;
    }
  | {
      readonly actionId: string;
      readonly kind: "replace_memory";
      readonly label: string;
    };

/**
 * Durable / reconstructable UI overlays formerly held only in RelayEngine memory.
 * Sensitive prose stays in artifacts; domain events hold only structural refs.
 */
export class OverlayState {
  constructor(
    private readonly store: EngineStore,
    private readonly clock: Clock,
    private readonly sessionId: string,
    private readonly putExpansion: (text: string) => Promise<{ artifactId: string; sha256: string }>,
    private readonly getExpansion: (artifactId: string, sha256: string) => Promise<string>,
  ) {}

  async stageAction(card: ActionCard): Promise<void> {
    const ref = await this.toStagedRef(card);
    await this.store.appendDomainEvent(ACTION_STAGED, this.clock.now().toISOString(), { ref });
  }

  async clearActions(match: (card: ActionCard) => boolean): Promise<void> {
    const pending = await this.listActions();
    const at = this.clock.now().toISOString();
    for (const card of pending) {
      if (!match(card)) continue;
      await this.store.appendDomainEvent(ACTION_CLEARED, at, { actionId: card.actionId });
    }
  }

  async listActions(): Promise<ActionCard[]> {
    const events = await this.store.listDomainEvents(OVERLAY_SCAN);
    const refs = new Map<string, StagedActionRef>();
    for (const event of events) {
      if (event.type === ACTION_STAGED) {
        const ref = event.payload.ref;
        if (isStagedRef(ref)) refs.set(ref.actionId, ref);
      } else if (event.type === ACTION_CLEARED) {
        const actionId = event.payload.actionId;
        if (typeof actionId === "string") refs.delete(actionId);
      }
    }
    const cards: ActionCard[] = [];
    for (const ref of refs.values()) {
      cards.push(await this.fromStagedRef(ref));
    }
    return cards;
  }

  async setInputPreview(text: string, caseId?: string): Promise<void> {
    const packed = await this.putExpansion(text);
    await this.store.appendDomainEvent(INPUT_PREVIEW, this.clock.now().toISOString(), {
      sessionId: this.sessionId,
      textArtifactId: packed.artifactId,
      textSha256: packed.sha256,
      ...(caseId ? { caseId } : {}),
    });
  }

  async clearInputPreview(caseId?: string): Promise<void> {
    await this.store.appendDomainEvent(INPUT_PREVIEW_CLEARED, this.clock.now().toISOString(), {
      sessionId: this.sessionId,
      ...(caseId ? { caseId } : {}),
    });
  }

  /**
   * Reconstruct preview: prefer durable overlay events; else latest typed source
   * whose case is still unfinished.
   */
  async getInputPreview(): Promise<string | null> {
    const fromEvents = await this.previewFromEvents();
    if (fromEvents !== undefined) return fromEvents;

    const [segments, cases] = await Promise.all([
      this.store.listSourceSegments(this.sessionId),
      this.store.listActiveCases(),
    ]);
    const unfinished = cases.filter((c) => c.status === "active" || c.status === "waiting");
    if (unfinished.length === 0) return null;
    const typed = [...segments].filter((s) => s.origin === "typed").sort((a, b) => b.sequence - a.sequence);
    return typed[0]?.text ?? null;
  }

  /** Option labels reconstruct from receipts (artifact-backed) — no domain-event copy. */
  async getOptionLabels(gateId: string): Promise<Record<string, string>> {
    const receipts = await this.store.learning.listReceipts();
    const fromReceipt = [...receipts].reverse().find((r) => r.gateId === gateId);
    if (fromReceipt && Object.keys(fromReceipt.optionLabels).length > 0) {
      return { ...fromReceipt.optionLabels };
    }
    return {};
  }

  private async previewFromEvents(): Promise<string | null | undefined> {
    const events = await this.store.listDomainEvents(OVERLAY_SCAN);
    let preview: string | null | undefined = undefined;
    for (const event of events) {
      if (event.type === INPUT_PREVIEW && event.payload.sessionId === this.sessionId) {
        const artifactId = event.payload.textArtifactId;
        const sha256 = event.payload.textSha256;
        if (typeof artifactId === "string" && typeof sha256 === "string") {
          try {
            preview = await this.getExpansion(artifactId, sha256);
          } catch {
            preview = null;
          }
        } else {
          preview = null;
        }
      } else if (event.type === INPUT_PREVIEW_CLEARED && event.payload.sessionId === this.sessionId) {
        preview = null;
      }
    }
    return preview;
  }

  private async toStagedRef(card: ActionCard): Promise<StagedActionRef> {
    if (card.kind === "save_definition") {
      const packed = await this.putExpansion(card.expansion ?? "");
      return {
        actionId: card.actionId,
        kind: "save_definition",
        label: card.label,
        token: card.token ?? "",
        expansionArtifactId: packed.artifactId,
        expansionSha256: packed.sha256,
      };
    }
    if (card.kind === "confirm_birthday") {
      return {
        actionId: card.actionId,
        kind: "confirm_birthday",
        label: card.label,
        personId: card.personId ?? "",
        displayName: card.displayName ?? "",
        date: card.date ?? "",
      };
    }
    return {
      actionId: card.actionId,
      kind: "replace_memory",
      label: card.label,
    };
  }

  private async fromStagedRef(ref: StagedActionRef): Promise<ActionCard> {
    if (ref.kind === "save_definition") {
      let expansion = "";
      try {
        expansion = await this.getExpansion(ref.expansionArtifactId, ref.expansionSha256);
      } catch {
        expansion = "";
      }
      return {
        actionId: ref.actionId,
        kind: "save_definition",
        label: ref.label,
        token: ref.token,
        expansion,
      };
    }
    if (ref.kind === "confirm_birthday") {
      return {
        actionId: ref.actionId,
        kind: "confirm_birthday",
        label: ref.label,
        personId: ref.personId,
        displayName: ref.displayName,
        date: ref.date,
      };
    }
    return {
      actionId: ref.actionId,
      kind: "replace_memory",
      label: ref.label,
    };
  }
}

function isStagedRef(value: unknown): value is StagedActionRef {
  if (!value || typeof value !== "object") return false;
  const ref = value as Partial<StagedActionRef>;
  return typeof ref.actionId === "string" && typeof ref.kind === "string" && typeof ref.label === "string";
}
