import type { FeedItemSnapshot } from "@relay/contracts";
import { ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly items: readonly FeedItemSnapshot[];
};

export function Feed({ items }: Props) {
  return (
    <ScrollView style={styles.scroll} contentContainerStyle={styles.content}>
      {items.length === 0 ? (
        <Text style={styles.empty}>No feed items yet. Ask a question or enable listening.</Text>
      ) : (
        items.map((item) => (
          <View key={item.itemId} style={styles.item}>
            <View style={styles.meta}>
              <Text style={styles.kind}>{item.kind}</Text>
              <Text style={styles.time}>{formatTime(item.createdAt)}</Text>
            </View>
            <Text style={styles.summary}>{item.summary}</Text>
            {item.caseId ? <Text style={styles.caseId}>{item.caseId}</Text> : null}
          </View>
        ))
      )}
    </ScrollView>
  );
}

function formatTime(iso: string): string {
  const d = Date.parse(iso);
  if (Number.isNaN(d)) return iso;
  return new Date(d).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

const styles = StyleSheet.create({
  scroll: {
    flex: 1,
  },
  content: {
    paddingHorizontal: 16,
    paddingBottom: 16,
    gap: 10,
  },
  empty: {
    color: colors.textDim,
    fontSize: 14,
    textAlign: "center",
    marginTop: 40,
    paddingHorizontal: 24,
  },
  item: {
    backgroundColor: colors.bgElevated,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    padding: 12,
  },
  meta: {
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 6,
  },
  kind: {
    color: colors.accent,
    fontSize: 11,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.6,
  },
  time: {
    color: colors.textDim,
    fontSize: 11,
  },
  summary: {
    color: colors.text,
    fontSize: 15,
    lineHeight: 21,
  },
  caseId: {
    color: colors.textMuted,
    fontSize: 11,
    marginTop: 6,
  },
});
