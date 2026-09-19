import { useState } from "react";
import type { RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
  readonly onOpenLog?: () => void;
};

const SPEEDS = [0, 1, 10] as const;

export function DeveloperConsole({
  snapshot,
  traceLines = [],
  onReplayFixture,
  onStartSession,
  onEndSession,
  onOpenLog,
}: Props) {
  const [speed, setSpeed] = useState<number>(0);
  const [paused, setPaused] = useState(false);
  const [frozen, setFrozen] = useState(snapshot.trace);
  const rows = paused ? frozen : snapshot.trace;
  const runtime = snapshot.runtime;
  const review = snapshot.review;

  return (
    <ScrollView style={styles.panel} contentContainerStyle={styles.content}>
      <Text style={styles.title}>Run inspector</Text>
      <Text style={styles.meta}>{`${runtime.runId} · ${runtime.commit} · ${runtime.mode}`}</Text>
      <Text style={styles.meta}>
        {`session ${runtime.sessionId ?? "none"} · episode ${runtime.episodeId ?? "none"} · queue ${runtime.queueDepth}`}
      </Text>
      <View style={styles.chips}>
        {snapshot.status.map((chip) => (
          <Text key={chip.id} style={styles.chip}>
            {`${chip.ok ? "●" : "○"} ${chip.label} ${chip.detail}`}
          </Text>
        ))}
      </View>
      <Text style={styles.path}>{runtime.logPath || "No run folder"}</Text>
      <Text style={styles.meta}>
        {runtime.logWritable ? `retention ${runtime.retention}` : `log error ${runtime.logError ?? "unavailable"}`}
      </Text>
      <View style={styles.row}>
        <Pressable onPress={onOpenLog} style={styles.button}>
          <Text style={styles.buttonText}>Open run folder</Text>
        </Pressable>
        <Pressable onPress={onStartSession} style={styles.button}>
          <Text style={styles.buttonText}>Start session</Text>
        </Pressable>
        <Pressable onPress={onEndSession} style={styles.button}>
          <Text style={styles.buttonText}>End session</Text>
        </Pressable>
      </View>

      <Text style={styles.section}>Current decision</Text>
      {snapshot.gate ? (
        <View style={styles.card}>
          <Line label="Gate" value={`${snapshot.gate.gateId} · ${snapshot.gate.policyVersion}`} />
          <Line label="Question" value={snapshot.gate.questionType} />
          <Line label="Options" value={snapshot.gate.optionIds.join(", ") || "none"} />
          <Line label="Probabilities" value={formatMap(snapshot.gate.probabilities)} />
          <Line label="Top / margin" value={`${formatNum(snapshot.gate.topProbability)} / ${formatNum(snapshot.gate.margin)}`} />
          <Line label="Threshold" value={formatNum(snapshot.gate.threshold)} />
          <Line label="Result" value={`${snapshot.gate.result} · ${snapshot.gate.reasonCode}`} />
          <Line label="Provider" value={snapshot.gate.provider} />
          <Line label="Latency / retries" value={`${snapshot.gate.latencyMs ?? "n/a"} ms · ${snapshot.gate.retries}`} />
          <Line label="Next" value={snapshot.gate.nextAction} />
        </View>
      ) : (
        <Text style={styles.empty}>No receipt yet.</Text>
      )}

      <Text style={styles.section}>Evidence</Text>
      {snapshot.patterns.length === 0 ? (
        <Text style={styles.empty}>No completed episodes yet.</Text>
      ) : (
        snapshot.patterns.map((pattern) => (
          <View key={pattern.signature} style={styles.card}>
            <Text style={styles.signature}>{pattern.signature}</Text>
            <Line label="Count / sessions" value={`${pattern.count} / ${pattern.sessions}`} />
            <Line label="State" value={pattern.candidateState ?? "observing"} />
            <Line label="Needed" value={pattern.needed || "none"} />
            <Line label="Because" value={pattern.because || "not proposed"} />
            <Line label="Evidence" value={pattern.evidenceIds.join(", ")} />
          </View>
        ))
      )}

      <Text style={styles.section}>Self-review</Text>
      <Text style={styles.meta}>
        {`sessions ${review.completeSessions}/${review.sessionTrigger} · reflexes ${review.approvedReflexes}/${review.reflexTrigger} · episodes ${review.completeEpisodes}/${review.episodeTrigger} · candidates ${review.qualifiedCandidates}/${review.candidateTrigger}`}
      </Text>
      <Text style={styles.meta}>
        {review.reviewDue ? `Review due · ${review.trigger}. Recommendations only.` : "No review trigger yet."}
      </Text>

      <View style={styles.row}>
        <Text style={styles.section}>Trace</Text>
        <Pressable
          onPress={() => {
            setFrozen(snapshot.trace);
            setPaused((value) => !value);
          }}
          style={styles.button}
        >
          <Text style={styles.buttonText}>{paused ? "Resume" : "Pause"}</Text>
        </Pressable>
      </View>
      {rows.length === 0 ? <Text style={styles.empty}>No canonical events yet.</Text> : null}
      {[...rows].reverse().map((line) => (
        <Text key={line.sequence} style={styles.trace}>
          {`${line.type} · ${line.reasonCode ?? "none"} · ${line.result ?? ""}`}
        </Text>
      ))}
      {traceLines.map((line, index) => (
        <Text key={`live-${index}`} style={styles.trace}>
          {line}
        </Text>
      ))}

      <View style={styles.row}>
        <Text style={styles.meta}>glossary fixture</Text>
        {SPEEDS.map((value) => (
          <Pressable key={value} onPress={() => setSpeed(value)} style={styles.button}>
            <Text style={styles.buttonText}>{value === 0 ? "0×" : `${value}×`}</Text>
          </Pressable>
        ))}
        <Pressable onPress={() => onReplayFixture?.("acronym-basic", speed)} style={styles.button}>
          <Text style={styles.buttonText}>Replay</Text>
        </Pressable>
      </View>
    </ScrollView>
  );
}

function Line({ label, value }: { readonly label: string; readonly value: string }) {
  return <Text style={styles.line}>{`${label}: ${value}`}</Text>;
}

function formatMap(values: Readonly<Record<string, number>>): string {
  const entries = Object.entries(values);
  if (entries.length === 0) return "none";
  return entries.map(([key, value]) => `${key} ${value.toFixed(2)}`).join(" · ");
}

function formatNum(value: number | null): string {
  return value == null ? "n/a" : value.toFixed(2);
}

const styles = StyleSheet.create({
  panel: { flex: 1, backgroundColor: colors.console, minWidth: 420 },
  content: { padding: 18, gap: 8 },
  title: { color: colors.text, fontSize: 22, fontWeight: "700" },
  section: { color: colors.text, fontSize: 14, fontWeight: "700", marginTop: 8 },
  meta: { color: colors.textMuted, fontSize: 12 },
  path: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  chip: { color: colors.textMuted, fontSize: 12 },
  row: { flexDirection: "row", flexWrap: "wrap", gap: 8, alignItems: "center" },
  button: { borderWidth: 1, borderColor: colors.borderStrong, borderRadius: 8, paddingHorizontal: 8, paddingVertical: 4 },
  buttonText: { color: colors.text, fontSize: 12 },
  card: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: 8, gap: 2 },
  signature: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  line: { color: colors.text, fontSize: 12 },
  empty: { color: colors.textDim, fontSize: 12 },
  trace: { color: colors.text, fontSize: 12, fontFamily: "monospace" },
});
