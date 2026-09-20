import { useState } from "react";
import type { PatternView, RelaySnapshot, TraceRow } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
  readonly onOpenLog?: () => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
};

const SPEEDS = [0, 1, 10] as const;

export function DeveloperConsole({
  snapshot,
  onReplayFixture,
  onStartSession,
  onEndSession,
  onOpenLog,
  onApproveCandidate,
  onRejectCandidate,
  onSnoozeCandidate,
}: Props) {
  const [speed, setSpeed] = useState<number>(0);
  const [paused, setPaused] = useState(false);
  const [frozen, setFrozen] = useState(snapshot.trace);
  const [stageFilter, setStageFilter] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [reasonFilter, setReasonFilter] = useState<string | null>(null);

  const rows = paused ? frozen : snapshot.trace;
  const runtime = snapshot.runtime;
  const review = snapshot.review;

  const filtered = rows.filter((row) => {
    if (stageFilter && row.stage !== stageFilter) return false;
    if (statusFilter && row.status !== statusFilter) return false;
    if (reasonFilter && row.reasonCode !== reasonFilter) return false;
    return true;
  });

  const stageChips = uniqueValues(rows.map((r) => r.stage));
  const statusChips = uniqueValues(rows.map((r) => r.status));
  const reasonChips = uniqueValues(rows.map((r) => r.reasonCode));

  const chronological = [...filtered].sort((a, b) => a.sequence - b.sequence);

  return (
    <ScrollView style={styles.panel} contentContainerStyle={styles.content}>
      <Text style={styles.title}>Run inspector</Text>
      <Text style={styles.meta}>
        {`${runtime.runId} · ${runtime.commit} · ${runtime.storageAdapter}`}
      </Text>
      <Text style={styles.path}>{runtime.logPath || "No run folder"}</Text>
      <View style={styles.chips}>
        {snapshot.status.map((chip) => (
          <Text key={chip.id} style={styles.chip}>
            {`${chip.ok ? "●" : "○"} ${chip.label} ${chip.detail}`}
          </Text>
        ))}
      </View>
      <Text style={styles.meta}>
        {`session ${runtime.sessionId ?? "none"} · episode ${runtime.episodeId ?? "none"} · case ${runtime.activeCaseId ?? "none"}`}
      </Text>
      <Text style={styles.meta}>
        {`queue ${runtime.queueDepth} · deadLetters ${runtime.deadLetters} · mode ${runtime.mode}`}
      </Text>
      <Text style={styles.meta}>
        {runtime.logWritable
          ? `retention ${runtime.retention}`
          : `log error ${runtime.logError ?? "unavailable"}`}
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
          <Line label="Thresholds" value={formatMap(snapshot.gate.thresholds)} />
          {Object.keys(snapshot.gate.optionLabels).length > 0 ? (
            <Line label="Option labels" value={formatStringMap(snapshot.gate.optionLabels)} />
          ) : null}
          <Line label="Options" value={snapshot.gate.optionIds.join(", ") || "none"} />
          <Line label="Probabilities" value={formatMap(snapshot.gate.probabilities)} />
          <Line
            label="Top / margin"
            value={`${formatNum(snapshot.gate.topProbability)} / ${formatNum(snapshot.gate.margin)}`}
          />
          <Line label="Selected" value={selectedOption(snapshot.gate.probabilities) ?? "n/a"} />
          <Line label="Result" value={`${snapshot.gate.result} · ${snapshot.gate.reasonCode}`} />
          <Line label="Provider" value={snapshot.gate.provider} />
          <Line label="Attempt" value={String(snapshot.gate.retries)} />
          <Line label="Latency" value={snapshot.gate.latencyMs != null ? `${snapshot.gate.latencyMs} ms` : "n/a"} />
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
          <PatternCard
            key={pattern.signature}
            pattern={pattern}
            {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
            {...(onRejectCandidate !== undefined ? { onRejectCandidate } : {})}
            {...(onSnoozeCandidate !== undefined ? { onSnoozeCandidate } : {})}
          />
        ))
      )}

      <Text style={styles.section}>Self-review</Text>
      <Text style={styles.meta}>
        {`sessions ${review.completeSessions}/${review.sessionTrigger} · approved ${review.approvedCandidates} · built ${review.builtReflexes}/${review.reflexTrigger} · active ${review.activeReflexes} · episodes ${review.completeEpisodes}/${review.episodeTrigger} · candidates ${review.qualifiedCandidates}/${review.candidateTrigger}`}
      </Text>
      <Text style={styles.meta}>
        {review.reviewDue
          ? `Review due · ${review.trigger}. Recommendations only.`
          : "No review trigger yet."}
      </Text>

      <View style={styles.row}>
        <Text style={styles.section}>Timeline</Text>
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

      <FilterRow
        label="stage"
        values={stageChips}
        active={stageFilter}
        onSelect={setStageFilter}
      />
      <FilterRow
        label="status"
        values={statusChips}
        active={statusFilter}
        onSelect={setStatusFilter}
      />
      <FilterRow
        label="reason"
        values={reasonChips}
        active={reasonFilter}
        onSelect={setReasonFilter}
      />

      <Text style={styles.traceHeader}>
        {"time | +delta | duration | stage | status | case | episode | reason"}
      </Text>
      {chronological.length === 0 ? <Text style={styles.empty}>No canonical events yet.</Text> : null}
      {[...chronological].reverse().map((row, index, arr) => {
        const older = arr[index + 1];
        return (
          <Text key={row.sequence} style={styles.trace}>
            {formatTraceLine(row, older)}
          </Text>
        );
      })}

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

function PatternCard({
  pattern,
  onApproveCandidate,
  onRejectCandidate,
  onSnoozeCandidate,
}: {
  readonly pattern: PatternView;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
}) {
  const rejected = rejectedOutcomes(pattern.outcomes);
  const showActions = pattern.candidateState === "proposed" && pattern.candidateId != null;
  const candidateId = pattern.candidateId ?? "";

  return (
    <View style={styles.card}>
      <Text style={styles.signature}>{pattern.signature}</Text>
      <Line label="Count / sessions" value={`${pattern.count} / ${pattern.sessions}`} />
      {rejected ? <Line label="Rejected outcomes" value={rejected} /> : null}
      <Line label="State" value={pattern.candidateState ?? "observing"} />
      <Line label="Needed" value={pattern.needed || "none"} />
      <Line label="Because" value={pattern.because || "not proposed"} />
      {showActions ? (
        <View style={styles.row}>
          <Pressable onPress={() => onApproveCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Approve</Text>
          </Pressable>
          <Pressable onPress={() => onRejectCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Reject</Text>
          </Pressable>
          <Pressable onPress={() => onSnoozeCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Snooze</Text>
          </Pressable>
        </View>
      ) : null}
    </View>
  );
}

function FilterRow({
  label,
  values,
  active,
  onSelect,
}: {
  readonly label: string;
  readonly values: readonly string[];
  readonly active: string | null;
  readonly onSelect: (value: string | null) => void;
}) {
  if (values.length === 0) return null;
  return (
    <View style={styles.row}>
      <Text style={styles.meta}>{label}</Text>
      <Pressable
        onPress={() => onSelect(null)}
        style={[styles.button, active === null ? styles.buttonActive : null]}
      >
        <Text style={styles.buttonText}>all</Text>
      </Pressable>
      {values.map((value) => (
        <Pressable
          key={value}
          onPress={() => onSelect(active === value ? null : value)}
          style={[styles.button, active === value ? styles.buttonActive : null]}
        >
          <Text style={styles.buttonText}>{value}</Text>
        </Pressable>
      ))}
    </View>
  );
}

function Line({ label, value }: { readonly label: string; readonly value: string }) {
  return <Text style={styles.line}>{`${label}: ${value}`}</Text>;
}

function formatTraceLine(row: TraceRow, previous: TraceRow | undefined): string {
  const time = formatTime(row.at);
  const delta =
    previous != null ? `+${Math.max(0, Date.parse(row.at) - Date.parse(previous.at))}ms` : "+0ms";
  const duration = row.durationMs != null ? `${row.durationMs}ms` : "-";
  const stage = row.stage ?? "-";
  const status = row.status ?? "-";
  const caseId = row.caseId ?? "-";
  const episode = row.episodeId ?? "-";
  const reason = row.reasonCode ?? "-";
  return `${time} | ${delta} | ${duration} | ${stage} | ${status} | ${caseId} | ${episode} | ${reason}`;
}

function formatTime(at: string): string {
  const ms = Date.parse(at);
  if (Number.isNaN(ms)) return at;
  return new Date(ms).toISOString().slice(11, 23);
}

function formatMap(values: Readonly<Record<string, number>>): string {
  const entries = Object.entries(values);
  if (entries.length === 0) return "none";
  return entries.map(([key, value]) => `${key} ${value.toFixed(2)}`).join(" · ");
}

function formatStringMap(values: Readonly<Record<string, string>>): string {
  const entries = Object.entries(values);
  if (entries.length === 0) return "none";
  return entries.map(([key, value]) => `${key}=${value}`).join(" · ");
}

function formatNum(value: number | null): string {
  return value == null ? "n/a" : value.toFixed(2);
}

function selectedOption(probabilities: Readonly<Record<string, number>>): string | null {
  const entries = Object.entries(probabilities);
  if (entries.length === 0) return null;
  return entries.reduce((best, cur) => (cur[1] > best[1] ? cur : best))[0];
}

function rejectedOutcomes(outcomes: Readonly<Record<string, number>>): string | null {
  const rejected = Object.entries(outcomes).filter(
    ([key, count]) => count > 0 && /reject|fail|deny/i.test(key),
  );
  if (rejected.length === 0) return null;
  return rejected.map(([key, count]) => `${key} ${count}`).join(" · ");
}

function uniqueValues(values: readonly (string | null)[]): string[] {
  return [...new Set(values.filter((value): value is string => value != null && value !== ""))];
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
  button: {
    borderWidth: 1,
    borderColor: colors.borderStrong,
    borderRadius: 8,
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  buttonActive: { borderColor: colors.cyan, backgroundColor: colors.accentSoft },
  buttonText: { color: colors.text, fontSize: 12 },
  card: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: 8, gap: 2 },
  signature: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  line: { color: colors.text, fontSize: 12 },
  empty: { color: colors.textDim, fontSize: 12 },
  traceHeader: { color: colors.textMuted, fontSize: 11, fontFamily: "monospace", marginTop: 4 },
  trace: { color: colors.text, fontSize: 12, fontFamily: "monospace" },
});
