/** On-device model delivery. No pin means download stays off. */

export const MAX_MODEL_DOWNLOAD_BYTES = 1_200_000_000;

export const UNSELECTED_MODEL_MESSAGE =
  "An on-device model is not installed. Download stays off until a tested model is selected.";

export type ModelPin = {
  readonly id: string;
  readonly version: string;
  readonly license: string;
  readonly byteSize: number;
  readonly sha256: string;
};

export type ModelDeliveryPhase =
  | "unselected"
  | "available"
  | "downloading"
  | "paused"
  | "verifying"
  | "installed"
  | "rejected";

export type ModelDeliveryView = {
  readonly phase: ModelDeliveryPhase;
  readonly pin: ModelPin | null;
  readonly bytesReceived: number;
  readonly message: string;
  readonly wifiRecommended: boolean;
};

export type DeliveryAction = "download" | "pause" | "resume" | "cancel" | "delete";

export function unselectedModelDelivery(): ModelDeliveryView {
  return {
    phase: "unselected",
    pin: null,
    bytesReceived: 0,
    message: UNSELECTED_MODEL_MESSAGE,
    wifiRecommended: false,
  };
}

export function formatModelBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "0 B";
  if (bytes < 1024) return `${Math.floor(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/** Size line for a pinned download. Raw byte counts stay in the delivery record. */
export function formatModelDeliveryProgress(
  view: Pick<ModelDeliveryView, "bytesReceived" | "pin" | "wifiRecommended">,
): string {
  const total = view.pin?.byteSize ?? 0;
  const wifi = view.wifiRecommended ? " · Use Wi-Fi" : "";
  return `${formatModelBytes(view.bytesReceived)} / ${formatModelBytes(total)}${wifi}`;
}

/** Version, license, and content hash for a pinned model. */
export function formatModelDeliveryIdentity(pin: ModelPin): string {
  return `${pin.version} · ${pin.license} · ${pin.sha256}`;
}

export function deliveryActions(phase: ModelDeliveryPhase): readonly DeliveryAction[] {
  switch (phase) {
    case "available":
    case "rejected":
      return ["download"];
    case "downloading":
    case "verifying":
      return ["pause", "cancel"];
    case "paused":
      return ["resume", "cancel"];
    case "installed":
      return ["delete"];
    default:
      return [];
  }
}
