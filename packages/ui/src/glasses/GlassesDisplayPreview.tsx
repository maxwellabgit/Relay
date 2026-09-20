import { StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

export type DisplayFrame = {
  readonly sequence: number;
  readonly lines: readonly string[];
};

type Props = {
  readonly frames: readonly DisplayFrame[];
  readonly connected: boolean;
  readonly maxCharsPerLine?: number;
  readonly maxLines?: number;
  readonly widthPx?: number;
  readonly heightPx?: number;
};

/**
 * Constrained preview of glasses display frames for protocol simulation.
 * Consumes the same frame payloads a hardware adapter would receive.
 */
export function GlassesDisplayPreview({
  frames,
  connected,
  maxCharsPerLine = 28,
  maxLines = 4,
  widthPx = 280,
  heightPx = 96,
}: Props) {
  const latest = frames.at(-1) ?? null;
  const lines = (latest?.lines ?? []).slice(0, maxLines).map((line) =>
    line.length > maxCharsPerLine ? `${line.slice(0, maxCharsPerLine - 1)}…` : line,
  );
  while (lines.length < maxLines) lines.push("");

  return (
    <View style={[styles.root, { width: widthPx }]}>
      <Text style={styles.meta}>{connected ? "connected" : "disconnected"} · preview only</Text>
      <View style={[styles.screen, { height: heightPx }]}>
        {lines.map((line, index) => (
          <Text key={`line-${index}`} style={styles.line} numberOfLines={1}>
            {line || " "}
          </Text>
        ))}
      </View>
      <Text style={styles.seq}>{latest ? `frame ${latest.sequence}` : "no frames"}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { gap: 6 },
  meta: { color: colors.textMuted, fontSize: 11 },
  screen: {
    borderWidth: 1,
    borderColor: colors.borderStrong,
    backgroundColor: "#05080d",
    paddingHorizontal: 8,
    paddingVertical: 6,
    justifyContent: "space-between",
  },
  line: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  seq: { color: colors.textDim, fontSize: 11 },
});
