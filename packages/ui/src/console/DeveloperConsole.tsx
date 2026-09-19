import { useState } from "react";
import type { ActivityLine, RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
};

const SPEEDS = [0, 1, 10] as const;

export function DeveloperConsole({ snapshot, onReplayFixture }: Props) {
  const [speed, setSpeed] = useState<number>(0);
  const engine = snapshot.status.find((chip) => chip.id === "engine");
  const lines = [...snapshot.activity].reverse();

  return (
    <View style={styles.panel}>
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.title}>Developer Console</Text>
          <Text style={styles.subtitle}>What RELAY is doing right now.</Text>
        </View>
        <View style={styles.connected}>
          <View style={[styles.liveDot, engine?.ok ? styles.liveOn : styles.liveOff]} />
          <Text style={styles.connectedLabel}>{engine?.ok ? "Running" : "Stopped"}</Text>
        </View>
      </View>

      <View style={styles.chips}>
        {snapshot.status.map((chip) => (
          <Text key={chip.id} style={styles.chip}>
            <Text style={chip.ok ? styles.ok : styles.dim}>{chip.ok ? "●" : "○"}</Text>
            {` ${chip.label} ${chip.detail}`}
          </Text>
        ))}
      </View>

      <View style={styles.replay}>
        <Text style={styles.replayLabel}>acronym-basic</Text>
        {SPEEDS.map((value) => (
          <Pressable
            key={value}
            onPress={() => setSpeed(value)}
            style={[styles.speedBtn, speed === value ? styles.speedOn : null]}
          >
            <Text style={styles.speedText}>{value === 0 ? "0×" : `${value}×`}</Text>
          </Pressable>
        ))}
        <Pressable onPress={() => onReplayFixture?.("acronym-basic", speed)} style={styles.playBtn}>
          <Text style={styles.playText}>Replay</Text>
        </Pressable>
      </View>

      <ScrollView contentContainerStyle={styles.log}>
        {lines.length === 0 ? (
          <Text style={styles.empty}>Waiting for the engine. Decisions will stream here.</Text>
        ) : (
          lines.map((line) => <LogLine key={line.sequence} line={line} />)
        )}
      </ScrollView>
    </View>
  );
}

function LogLine({ line }: { readonly line: ActivityLine }) {
  const tone = toneFor(line.eventType);
  return (
    <View style={styles.row}>
      <Text style={styles.time}>{formatTime(line.at)}</Text>
      <Text style={[styles.event, tone === "jev" ? styles.jev : null, tone === "bad" ? styles.bad : null]}>
        {line.eventType}
      </Text>
      <Text style={styles.message}>{line.message}</Text>
    </View>
  );
}

function toneFor(eventType: string): "jev" | "bad" | "plain" {
  if (eventType.startsWith("jev.") || eventType.startsWith("memory.")) return "jev";
  if (eventType.endsWith("failed") || eventType.endsWith("rejected") || eventType.endsWith("refused")) {
    return "bad";
  }
  return "plain";
}

function formatTime(iso: string): string {
  const parsed = Date.parse(iso);
  if (Number.isNaN(parsed)) return iso;
  return new Date(parsed).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

const styles = StyleSheet.create({
  panel: {
    flex: 1,
    backgroundColor: colors.console,
    minWidth: 420,
  },
  header: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    paddingHorizontal: 22,
    paddingTop: 18,
    paddingBottom: 8,
    gap: 12,
  },
  headerText: { flex: 1, gap: 4 },
  title: {
    color: colors.text,
    fontSize: 22,
    fontWeight: "700",
  },
  subtitle: {
    color: colors.textMuted,
    fontSize: 13,
  },
  connected: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingTop: 6,
  },
  liveDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  liveOn: { backgroundColor: colors.ok },
  liveOff: { backgroundColor: colors.textDim },
  connectedLabel: {
    color: colors.text,
    fontSize: 13,
    fontWeight: "600",
  },
  chips: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    paddingHorizontal: 22,
    paddingBottom: 8,
  },
  chip: {
    color: colors.textMuted,
    fontSize: 12,
  },
  ok: { color: colors.ok },
  dim: { color: colors.textDim },
  replay: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 22,
    paddingBottom: 10,
  },
  replayLabel: {
    color: colors.textMuted,
    fontSize: 12,
  },
  speedBtn: {
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 8,
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  speedOn: {
    borderColor: colors.cyan,
    backgroundColor: colors.accentSoft,
  },
  speedText: {
    color: colors.text,
    fontSize: 12,
  },
  playBtn: {
    backgroundColor: colors.blue,
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 6,
  },
  playText: {
    color: "#f7fbff",
    fontSize: 12,
    fontWeight: "700",
  },
  log: {
    paddingHorizontal: 18,
    paddingBottom: 24,
    gap: 2,
  },
  empty: {
    color: colors.textDim,
    fontSize: 13,
    paddingTop: 8,
  },
  row: {
    flexDirection: "row",
    gap: 10,
    paddingVertical: 6,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  time: {
    width: 78,
    color: colors.textDim,
    fontSize: 12,
    fontFamily: "monospace",
  },
  event: {
    width: 148,
    color: colors.textMuted,
    fontSize: 12,
    fontFamily: "monospace",
  },
  jev: { color: colors.cyan },
  bad: { color: colors.warn },
  message: {
    flex: 1,
    color: colors.text,
    fontSize: 13,
    lineHeight: 18,
  },
});
