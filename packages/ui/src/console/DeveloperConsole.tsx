import { useState } from "react";
import type { GateMark, RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
};

const SPEEDS = [0, 1, 10] as const;

export function DeveloperConsole({
  snapshot,
  onReplayFixture,
  onStartSession,
  onEndSession,
}: Props) {
  const [speed, setSpeed] = useState<number>(0);
  const engine = snapshot.status.find((chip) => chip.id === "engine");
  const expansion = snapshot.expansion;
  const decisions = [...snapshot.decisions].reverse();

  return (
    <View style={styles.panel}>
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.title}>Decision ledger</Text>
          <Text style={styles.subtitle}>Gates, Nouls, and what RELAY is allowed to keep.</Text>
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

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Bounded expansion</Text>
        <Meter
          label="Complete work sessions"
          value={expansion.completeSessions}
          target={expansion.sessionTarget}
        />
        <Meter label="Reflexes built" value={expansion.reflexesBuilt} target={expansion.reflexTarget} />
        <Text style={styles.review}>
          {expansion.reviewDue
            ? "Self-review is due."
            : "Self-review waits until both thresholds are met. Nothing is rewritten yet."}
        </Text>
        <View style={styles.sessionRow}>
          <Pressable onPress={onStartSession} style={styles.sessionBtn}>
            <Text style={styles.sessionText}>Start session</Text>
          </Pressable>
          <Pressable onPress={onEndSession} style={styles.sessionBtn}>
            <Text style={styles.sessionText}>End session</Text>
          </Pressable>
        </View>
      </View>

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>{snapshot.gate ? snapshot.gate.title : "No decision yet"}</Text>
        {snapshot.gate ? (
          snapshot.gate.rows.map((row) => (
            <View key={row.label} style={styles.gateRow}>
              <Text style={[styles.mark, markStyle(row.mark)]}>{markGlyph(row.mark)}</Text>
              <Text style={styles.gateLabel}>{row.label}</Text>
              <Text style={styles.gateValue}>{row.value}</Text>
            </View>
          ))
        ) : (
          <Text style={styles.empty}>Ask for an acronym, or add one to memory. The gate lands here.</Text>
        )}
      </View>

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Recommendations</Text>
        {snapshot.recommendations.length === 0 ? (
          <Text style={styles.empty}>None yet. A repeated lookup can become a candidate. It is not built.</Text>
        ) : (
          snapshot.recommendations.map((item) => (
            <Text key={`${item.code}:${item.because}`} style={styles.recommend}>
              {`${item.code} · ${item.because} · ${item.count} · ${item.status}`}
            </Text>
          ))
        )}
      </View>

      <Text style={styles.path}>{snapshot.decisionLogPath}</Text>
      <ScrollView contentContainerStyle={styles.log}>
        {decisions.length === 0 ? (
          <Text style={styles.empty}>Kept decisions stream here. Trash lines are dropped.</Text>
        ) : (
          decisions.map((line) => (
            <View key={line.sequence} style={styles.row}>
              <Text style={styles.time}>{formatTime(line.at)}</Text>
              <Text style={styles.event}>{line.code}</Text>
              <Text style={styles.message}>{line.detail}</Text>
            </View>
          ))
        )}
      </ScrollView>

      <View style={styles.replay}>
        <Text style={styles.replayLabel}>glossary fixture</Text>
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
    </View>
  );
}

function Meter({ label, value, target }: { readonly label: string; readonly value: number; readonly target: number }) {
  const ratio = target === 0 ? 0 : Math.min(1, value / target);
  return (
    <View style={styles.meter}>
      <Text style={styles.meterLabel}>{`${label} ${value}/${target}`}</Text>
      <View style={styles.track}>
        <View style={[styles.fill, { width: `${Math.round(ratio * 100)}%` }]} />
      </View>
    </View>
  );
}

function markGlyph(mark: GateMark): string {
  if (mark === "pass") return "●";
  if (mark === "fail") return "○";
  if (mark === "wait") return "…";
  return "·";
}

function markStyle(mark: GateMark) {
  if (mark === "pass") return styles.ok;
  if (mark === "fail") return styles.bad;
  if (mark === "wait") return styles.warn;
  return styles.dim;
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
  section: {
    marginHorizontal: 18,
    marginBottom: 10,
    padding: 12,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 10,
    backgroundColor: colors.bgPanel,
    gap: 6,
  },
  sectionTitle: {
    color: colors.text,
    fontSize: 14,
    fontWeight: "700",
  },
  review: {
    color: colors.textMuted,
    fontSize: 12,
    lineHeight: 16,
  },
  sessionRow: {
    flexDirection: "row",
    gap: 8,
  },
  sessionBtn: {
    borderWidth: 1,
    borderColor: colors.borderStrong,
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  sessionText: {
    color: colors.text,
    fontSize: 12,
    fontWeight: "600",
  },
  meter: { gap: 4 },
  meterLabel: {
    color: colors.textMuted,
    fontSize: 12,
  },
  track: {
    height: 6,
    borderRadius: 3,
    backgroundColor: colors.bg,
    overflow: "hidden",
  },
  fill: {
    height: 6,
    backgroundColor: colors.cyan,
  },
  gateRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  mark: {
    width: 16,
    fontSize: 12,
  },
  gateLabel: {
    width: 130,
    color: colors.textMuted,
    fontSize: 12,
  },
  gateValue: {
    flex: 1,
    color: colors.text,
    fontSize: 12,
  },
  recommend: {
    color: colors.cyan,
    fontSize: 12,
    fontFamily: "monospace",
  },
  path: {
    color: colors.textDim,
    fontSize: 11,
    fontFamily: "monospace",
    paddingHorizontal: 22,
    paddingBottom: 4,
  },
  log: {
    paddingHorizontal: 18,
    paddingBottom: 12,
    gap: 2,
  },
  empty: {
    color: colors.textDim,
    fontSize: 12,
  },
  row: {
    flexDirection: "row",
    gap: 10,
    paddingVertical: 5,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  time: {
    width: 78,
    color: colors.textDim,
    fontSize: 11,
    fontFamily: "monospace",
  },
  event: {
    width: 168,
    color: colors.cyan,
    fontSize: 11,
    fontFamily: "monospace",
  },
  message: {
    flex: 1,
    color: colors.text,
    fontSize: 12,
    lineHeight: 16,
  },
  ok: { color: colors.ok },
  dim: { color: colors.textDim },
  bad: { color: colors.danger },
  warn: { color: colors.warn },
  replay: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 22,
    paddingVertical: 10,
    borderTopWidth: 1,
    borderTopColor: colors.border,
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
});
