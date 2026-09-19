export type GlanceKind = "listening" | "caption" | "finding" | "status" | "clear";

export type GlanceFrame = {
  readonly kind: GlanceKind;
  readonly title?: string;
  readonly body?: string;
  readonly truncated?: boolean;
};

export type GlassesDisplayPort = {
  show(frame: GlanceFrame): Promise<void>;
  clear(): Promise<void>;
};
