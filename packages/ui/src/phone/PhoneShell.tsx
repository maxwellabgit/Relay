import type { FeedItemSnapshot, RelaySnapshot } from "@relay/contracts";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { Composer } from "../assistant/Composer.js";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onRemember?: (token: string) => void;
};

export function PhoneShell({ snapshot, onListenChange, onSubmit, onRemember }: Props) {
  const task = [...snapshot.feedItems].reverse().find((item) => item.kind === "task");
  const token = task ? acronymFromTask(task.summary) : null;
  const memory = [...snapshot.feedItems].reverse().find((item) => item.kind === "memory");
  const stored = memory?.summary.startsWith("Remembered ") === true;

  return (
    <View style={styles.bezel}>
      <View style={styles.screen}>
        <View style={styles.header}>
          <View style={styles.brandRow}>
            <View style={styles.mark}>
              <View style={[styles.bar, styles.barShort]} />
              <View style={[styles.bar, styles.barMid]} />
              <View style={[styles.bar, styles.barTall]} />
            </View>
            <View style={styles.brandText}>
              <Text style={styles.brand}>RELAY</Text>
              <Text style={styles.tagline}>Your AI teammate, on your terms</Text>
            </View>
          </View>
          <Text style={styles.gear}>⚙</Text>
        </View>

        <View style={styles.listenBlock}>
          <View style={styles.wave}>
            {[10, 18, 28, 16, 24, 12, 20].map((height, index) => (
              <View key={index} style={[styles.waveBar, { height }]} />
            ))}
          </View>
          <Pressable
            accessibilityRole="switch"
            accessibilityState={{ checked: snapshot.listening }}
            onPress={() => onListenChange(!snapshot.listening)}
            style={[styles.listenBtn, snapshot.listening ? styles.listenOn : styles.listenOff]}
          >
            <View style={[styles.dot, snapshot.listening ? styles.dotOn : styles.dotOff]} />
            <Text style={styles.listenLabel}>
              {snapshot.listening ? "Listening On" : "Listening Off"}
            </Text>
          </Pressable>
          <Text style={styles.listenHint}>
            {snapshot.listening
              ? "I'm listening. Speak naturally."
              : "Listening is off. Ask directly, or turn listening on."}
          </Text>
        </View>

        <ScrollView style={styles.thread} contentContainerStyle={styles.threadContent}>
          {snapshot.feedItems.length === 0 ? (
            <Text style={styles.empty}>
              Ask what an unknown acronym means. RELAY recommends a search instead of inventing a
              definition.
            </Text>
          ) : (
            snapshot.feedItems.map((item) => <Bubble key={item.itemId} item={item} />)
          )}
        </ScrollView>

        {task ? (
          <View style={styles.actions}>
            <View style={styles.actionCard}>
              <Text style={styles.actionEyebrow}>Recommended task</Text>
              <Text style={styles.actionTitle}>{task.summary}</Text>
            </View>
            {token ? (
              <Pressable
                accessibilityRole="button"
                disabled={stored}
                onPress={() => onRemember?.(token)}
                style={[styles.memory, stored ? styles.memoryStored : null]}
              >
                <Text style={styles.memoryLabel}>{stored ? "In memory" : "Add to memory"}</Text>
                <Text style={styles.memoryValue}>
                  {memory?.summary ?? `Remember '${token}' after Jev`}
                </Text>
              </Pressable>
            ) : null}
          </View>
        ) : null}

        <Composer onSubmit={onSubmit} />

        <View style={styles.tabs}>
          <Tab label="Home" active={false} />
          <Tab label="Listen" active />
          <Tab label="Cases" active={false} />
          <Tab label="Memory" active={false} />
        </View>
        <Text style={styles.footer}>Private. Local. In your control.</Text>
      </View>
    </View>
  );
}

function Bubble({ item }: { readonly item: FeedItemSnapshot }) {
  if (item.kind === "ask") {
    return (
      <View style={styles.userBubble}>
        <Text style={styles.userText}>{item.summary}</Text>
      </View>
    );
  }
  if (item.kind === "task") {
    return (
      <View style={styles.assistantBubble}>
        <Text style={styles.assistantName}>RELAY</Text>
        <Text style={styles.taskEyebrow}>Recommended task</Text>
        <Text style={styles.assistantText}>{item.summary}</Text>
      </View>
    );
  }
  if (item.kind === "memory") {
    return (
      <View style={styles.assistantBubble}>
        <Text style={styles.assistantName}>RELAY</Text>
        <Text style={styles.taskEyebrow}>Memory</Text>
        <Text style={styles.assistantText}>{item.summary}</Text>
      </View>
    );
  }
    return (
      <View style={styles.assistantBubble}>
        <Text style={styles.assistantName}>RELAY</Text>
        <Text style={styles.assistantText}>{item.summary}</Text>
      </View>
    );
  return null;
}

function Tab({ label, active }: { readonly label: string; readonly active: boolean }) {
  return (
    <View style={styles.tab}>
      <View style={[styles.tabMark, active ? styles.tabMarkOn : null]} />
      <Text style={[styles.tabLabel, active ? styles.tabLabelOn : null]}>{label}</Text>
    </View>
  );
}

function acronymFromTask(summary: string): string | null {
  const match = /definition of ([A-Z0-9]{2,12})/.exec(summary);
  return match?.[1] ?? null;
}

const styles = StyleSheet.create({
  bezel: {
    width: 390,
    maxHeight: "100%",
    flex: 1,
    borderRadius: 36,
    padding: 10,
    backgroundColor: "#05080e",
    borderWidth: 1,
    borderColor: "#1c2838",
  },
  screen: {
    flex: 1,
    borderRadius: 28,
    backgroundColor: colors.phone,
    overflow: "hidden",
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 18,
    paddingTop: 16,
    paddingBottom: 8,
  },
  brandRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  mark: {
    width: 22,
    height: 22,
    justifyContent: "flex-end",
    gap: 3,
  },
  bar: {
    height: 3,
    borderRadius: 2,
    backgroundColor: colors.cyan,
  },
  barShort: { width: 10 },
  barMid: { width: 16 },
  barTall: { width: 22 },
  brandText: { gap: 1 },
  brand: {
    color: colors.text,
    fontSize: 16,
    fontWeight: "800",
    letterSpacing: 1.4,
  },
  tagline: {
    color: colors.textMuted,
    fontSize: 11,
  },
  gear: {
    color: colors.textMuted,
    fontSize: 16,
  },
  listenBlock: {
    alignItems: "center",
    paddingHorizontal: 20,
    paddingBottom: 8,
    gap: 8,
  },
  wave: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    height: 32,
  },
  waveBar: {
    width: 4,
    borderRadius: 2,
    backgroundColor: colors.cyan,
  },
  listenBtn: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 20,
    paddingVertical: 8,
    paddingHorizontal: 14,
  },
  listenOn: { backgroundColor: colors.listenOn },
  listenOff: { backgroundColor: colors.listenOff },
  dot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  dotOn: { backgroundColor: "#d9ffe8" },
  dotOff: { backgroundColor: colors.textDim },
  listenLabel: {
    color: "#f4fff8",
    fontWeight: "700",
    fontSize: 13,
  },
  listenHint: {
    color: colors.textMuted,
    fontSize: 13,
    textAlign: "center",
  },
  thread: { flex: 1 },
  threadContent: {
    paddingHorizontal: 14,
    paddingBottom: 8,
    gap: 10,
  },
  empty: {
    color: colors.textDim,
    fontSize: 13,
    lineHeight: 18,
    textAlign: "center",
    marginTop: 12,
    paddingHorizontal: 12,
  },
  userBubble: {
    alignSelf: "flex-end",
    maxWidth: "88%",
    backgroundColor: colors.userBubble,
    borderRadius: 16,
    borderBottomRightRadius: 4,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  userText: {
    color: colors.text,
    fontSize: 14,
    lineHeight: 20,
  },
  assistantBubble: {
    alignSelf: "flex-start",
    maxWidth: "92%",
    backgroundColor: colors.bgElevated,
    borderRadius: 16,
    borderBottomLeftRadius: 4,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: 12,
    paddingVertical: 10,
    gap: 4,
  },
  assistantName: {
    color: colors.cyan,
    fontSize: 11,
    fontWeight: "800",
    letterSpacing: 0.8,
  },
  taskEyebrow: {
    color: colors.blue,
    fontSize: 11,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.6,
  },
  assistantText: {
    color: colors.text,
    fontSize: 14,
    lineHeight: 20,
  },
  actions: {
    paddingHorizontal: 14,
    paddingBottom: 8,
    gap: 8,
  },
  actionCard: {
    backgroundColor: colors.task,
    borderRadius: 14,
    borderWidth: 1,
    borderColor: "#2a4d86",
    padding: 12,
    gap: 4,
  },
  actionEyebrow: {
    color: colors.blue,
    fontSize: 11,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.5,
  },
  actionTitle: {
    color: colors.text,
    fontSize: 14,
    fontWeight: "600",
  },
  memory: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    backgroundColor: colors.bgElevated,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  memoryLabel: {
    color: colors.textMuted,
    fontSize: 12,
  },
  memoryStored: {
    borderColor: colors.ok,
  },
  memoryValue: {
    color: colors.text,
    fontSize: 12,
    fontWeight: "600",
    flex: 1,
    textAlign: "right",
    marginLeft: 8,
  },
  tabs: {
    flexDirection: "row",
    borderTopWidth: 1,
    borderTopColor: colors.border,
    paddingTop: 8,
    paddingBottom: 4,
  },
  tab: {
    flex: 1,
    alignItems: "center",
    gap: 4,
  },
  tabMark: {
    width: 16,
    height: 3,
    borderRadius: 2,
    backgroundColor: "transparent",
  },
  tabMarkOn: {
    backgroundColor: colors.cyan,
  },
  tabLabel: {
    color: colors.textDim,
    fontSize: 11,
  },
  tabLabelOn: {
    color: colors.cyan,
    fontWeight: "700",
  },
  footer: {
    color: colors.textDim,
    fontSize: 10,
    textAlign: "center",
    paddingBottom: 10,
  },
});
