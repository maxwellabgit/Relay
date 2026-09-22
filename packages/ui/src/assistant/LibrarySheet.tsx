import type { PatternView, ReflexStateSnapshot, RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly open: boolean;
  readonly snapshot: RelaySnapshot;
  readonly onClose: () => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onActivateReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onPauseReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onRollbackReflex?: (reflexId: string, version: number, stateVersion: number) => void;
};

/**
 * Library sheet — Memories + proposed/active Reflexes (not the chat timeline).
 */
export function LibrarySheet({
  open,
  snapshot,
  onClose,
  onApproveCandidate,
  onActivateReflex,
  onPauseReflex,
  onRollbackReflex,
}: Props) {
  if (!open) return null;

  const proposed = snapshot.patterns.filter((p) => p.candidateState === "proposed");
  const ready = snapshot.patterns.filter((p) => p.candidateState === "activation_ready");
  const memories = snapshot.memories;

  return (
    <View style={styles.backdrop}>
      <View style={styles.sheet}>
        <View style={styles.header}>
          <Text style={styles.title}>Library</Text>
          <Pressable accessibilityRole="button" accessibilityLabel="Close library" onPress={onClose}>
            <Text style={styles.close}>Close</Text>
          </Pressable>
        </View>
        <ScrollView contentContainerStyle={styles.body}>
          <Text style={styles.section}>Memories</Text>
          {memories.length === 0 ? (
            <Text style={styles.empty}>No saved memories yet.</Text>
          ) : (
            memories.slice(0, 20).map((memory) => (
              <Text key={`${memory.kind}:${memory.key}`} style={styles.row}>
                {`${memory.kind} · ${memory.key}`}
              </Text>
            ))
          )}

          <Text style={styles.section}>Proposed Reflexes</Text>
          {proposed.length === 0 ? (
            <Text style={styles.empty}>No proposals waiting for review.</Text>
          ) : (
            proposed.map((pattern) => (
              <ProposalRow
                key={pattern.signature}
                pattern={pattern}
                {...(onApproveCandidate ? { onApprove: onApproveCandidate } : {})}
              />
            ))
          )}

          <Text style={styles.section}>Ready to activate</Text>
          {ready.length === 0 ? (
            <Text style={styles.empty}>No built Reflexes awaiting activation.</Text>
          ) : (
            ready.map((pattern) => (
              <Text key={pattern.signature} style={styles.row}>
                {`${pattern.signature} · activation_ready`}
              </Text>
            ))
          )}

          <Text style={styles.section}>Active Reflexes</Text>
          {snapshot.reflexes.length === 0 ? (
            <Text style={styles.empty}>No Reflex authority state yet.</Text>
          ) : (
            snapshot.reflexes.map((reflex) => (
              <ReflexRow
                key={`${reflex.reflex.id}@${reflex.reflex.version}`}
                reflex={reflex}
                {...(onActivateReflex ? { onActivate: onActivateReflex } : {})}
                {...(onPauseReflex ? { onPause: onPauseReflex } : {})}
                {...(onRollbackReflex ? { onRollback: onRollbackReflex } : {})}
              />
            ))
          )}
        </ScrollView>
      </View>
    </View>
  );
}

function ProposalRow({
  pattern,
  onApprove,
}: {
  readonly pattern: PatternView;
  readonly onApprove?: (candidateId: string) => void;
}) {
  const candidateId = pattern.candidateId ?? "";
  return (
    <View style={styles.card}>
      <Text style={styles.row}>{pattern.because || pattern.signature}</Text>
      <Text style={styles.meta}>{`${pattern.count}× · ${pattern.sessions} sessions`}</Text>
      {candidateId ? (
        <Pressable
          accessibilityRole="button"
          onPress={() => onApprove?.(candidateId)}
          style={styles.button}
        >
          <Text style={styles.buttonLabel}>Approve for build</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

function ReflexRow({
  reflex,
  onActivate,
  onPause,
  onRollback,
}: {
  readonly reflex: ReflexStateSnapshot;
  readonly onActivate?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onPause?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onRollback?: (reflexId: string, version: number, stateVersion: number) => void;
}) {
  return (
    <View style={styles.card}>
      <Text style={styles.row}>{`${reflex.reflex.id}@${reflex.reflex.version}`}</Text>
      <Text style={styles.meta}>{`activation · ${reflex.activation}`}</Text>
      <View style={styles.actions}>
        {reflex.activation !== "active" ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => onActivate?.(reflex.reflex.id, reflex.reflex.version, reflex.stateVersion)}
            style={styles.button}
          >
            <Text style={styles.buttonLabel}>Activate</Text>
          </Pressable>
        ) : (
          <Pressable
            accessibilityRole="button"
            onPress={() => onPause?.(reflex.reflex.id, reflex.reflex.version, reflex.stateVersion)}
            style={styles.button}
          >
            <Text style={styles.buttonLabel}>Pause</Text>
          </Pressable>
        )}
        {reflex.activation === "active" || reflex.activation === "paused" ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => onRollback?.(reflex.reflex.id, reflex.reflex.version, reflex.stateVersion)}
            style={styles.buttonSecondary}
          >
            <Text style={styles.buttonLabel}>Rollback</Text>
          </Pressable>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    ...StyleSheet.absoluteFill,
    backgroundColor: "rgba(0,0,0,0.35)",
    justifyContent: "flex-end",
    zIndex: 20,
  },
  sheet: {
    maxHeight: "85%",
    backgroundColor: colors.bgElevated,
    borderTopLeftRadius: 16,
    borderTopRightRadius: 16,
    paddingBottom: 24,
  },
  header: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: colors.border,
  },
  title: { fontSize: 18, fontWeight: "600", color: colors.text },
  close: { fontSize: 15, color: colors.accent },
  body: { paddingHorizontal: 16, paddingTop: 12, paddingBottom: 32, gap: 8 },
  section: {
    marginTop: 12,
    fontSize: 13,
    fontWeight: "600",
    color: colors.textMuted,
    textTransform: "uppercase",
    letterSpacing: 0.6,
  },
  empty: { fontSize: 14, color: colors.textMuted, marginBottom: 4 },
  row: { fontSize: 14, color: colors.text },
  meta: { fontSize: 12, color: colors.textMuted, marginTop: 2 },
  card: {
    paddingVertical: 8,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: colors.border,
  },
  actions: { flexDirection: "row", gap: 8, marginTop: 8 },
  button: {
    alignSelf: "flex-start",
    backgroundColor: colors.accent,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 6,
    marginTop: 6,
  },
  buttonSecondary: {
    alignSelf: "flex-start",
    backgroundColor: colors.borderStrong,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 6,
    marginTop: 6,
  },
  buttonLabel: { color: colors.bg, fontSize: 13, fontWeight: "600" },
});
