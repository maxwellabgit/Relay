import type { ReactNode } from "react";
import type { RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
};

const FIXTURES = ["acronym-ask", "bess-glossary", "quiet-room"] as const;

export function DeveloperPanel({ snapshot, traceLines = [], onReplayFixture }: Props) {
  return (
    <View style={styles.panel}>
      <Text style={styles.heading}>Developer</Text>
      <ScrollView contentContainerStyle={styles.content}>
        <Section title="Status">
          <Text style={styles.mono}>
            listening={String(snapshot.listening)} queue={snapshot.queueDepth}
          </Text>
          {snapshot.status.map((s) => (
            <Text key={s.id} style={styles.mono}>
              {s.id}: {s.ok ? "ok" : "bad"} ({s.detail})
            </Text>
          ))}
        </Section>

        <Section title={`Cases (${snapshot.cases.length})`}>
          {snapshot.cases.length === 0 ? (
            <Text style={styles.dim}>none</Text>
          ) : (
            snapshot.cases.map((c) => (
              <Text key={c.caseId} style={styles.mono}>
                {c.caseId} · {c.phase}/{c.status} · v{c.version}
              </Text>
            ))
          )}
        </Section>

        <Section title={`Source segments (${snapshot.sourceSegments.length})`}>
          {snapshot.sourceSegments.length === 0 ? (
            <Text style={styles.dim}>none</Text>
          ) : (
            snapshot.sourceSegments.map((s) => (
              <Text key={s.segmentId} style={styles.mono}>
                #{s.sequence} {s.origin} {s.final ? "final" : "interim"} {s.text}
              </Text>
            ))
          )}
        </Section>

        <Section title="Fixture controls">
          <View style={styles.fixtureRow}>
            {FIXTURES.map((fixture) => (
              <Pressable
                key={fixture}
                style={styles.fixtureBtn}
                onPress={() => onReplayFixture?.(fixture, 1)}
              >
                <Text style={styles.fixtureText}>{fixture}</Text>
              </Pressable>
            ))}
          </View>
          <Text style={styles.dim}>Placeholders — wire to replay runner later.</Text>
        </Section>

        <Section title="Trace">
          {traceLines.length === 0 ? (
            <Text style={styles.dim}>no trace lines</Text>
          ) : (
            traceLines.map((line, i) => (
              <Text key={`${i}:${line}`} style={styles.mono}>
                {line}
              </Text>
            ))
          )}
        </Section>
      </ScrollView>
    </View>
  );
}

function Section({
  title,
  children,
}: {
  title: string;
  children: ReactNode;
}) {
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  panel: {
    flex: 1,
    backgroundColor: colors.bgPanel,
    borderLeftWidth: 1,
    borderLeftColor: colors.border,
    minWidth: 280,
  },
  heading: {
    color: colors.text,
    fontSize: 14,
    fontWeight: "700",
    letterSpacing: 1,
    paddingHorizontal: 14,
    paddingTop: 16,
    paddingBottom: 8,
    textTransform: "uppercase",
  },
  content: {
    paddingHorizontal: 14,
    paddingBottom: 24,
    gap: 16,
  },
  section: {
    gap: 6,
  },
  sectionTitle: {
    color: colors.textMuted,
    fontSize: 11,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.8,
    marginBottom: 2,
  },
  mono: {
    color: colors.text,
    fontSize: 11,
    fontFamily: "monospace",
    lineHeight: 16,
  },
  dim: {
    color: colors.textDim,
    fontSize: 11,
  },
  fixtureRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  fixtureBtn: {
    backgroundColor: colors.bgElevated,
    borderWidth: 1,
    borderColor: colors.borderStrong,
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  fixtureText: {
    color: colors.text,
    fontSize: 11,
  },
});
