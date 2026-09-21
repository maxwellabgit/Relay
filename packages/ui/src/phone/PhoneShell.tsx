import type { ActionCard, FeedItemSnapshot, RelaySnapshot } from "@relay/contracts";
import { useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { Composer } from "../assistant/Composer.js";
import { SettingsSheet } from "../assistant/SettingsSheet.js";
import { colors } from "../theme/colors.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onAction?: (action: ActionCard) => void;
  readonly typeSafeKeyStatus?: "present" | "disabled" | "unknown";
  readonly onSetTypeSafeKey?: (value: string) => Promise<void>;
  readonly onDeleteTypeSafeKey?: () => Promise<void>;
  readonly onSetHostedProcessing?: (enabled: boolean) => void;
  readonly onRefreshHealth?: () => void;
};

export function PhoneShell({
  snapshot,
  onListenChange,
  onSubmit,
  onAction,
  typeSafeKeyStatus = "unknown",
  onSetTypeSafeKey,
  onDeleteTypeSafeKey,
  onSetHostedProcessing,
  onRefreshHealth,
}: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false);
  const threadRef = useRef<ScrollView>(null);

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
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Open settings"
            onPress={() => setSettingsOpen(true)}
            hitSlop={8}
          >
            <Text style={styles.gear}>⚙</Text>
          </Pressable>
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

        <ScrollView
          ref={threadRef}
          style={styles.thread}
          contentContainerStyle={styles.threadContent}
          onContentSizeChange={() => {
            threadRef.current?.scrollToEnd({ animated: false });
          }}
        >
          {snapshot.feedItems.length === 0 ? (
            <Text style={styles.empty}>
              Ask what an unknown acronym means. RELAY recommends a search instead of inventing a
              definition.
            </Text>
          ) : (
            snapshot.feedItems.map((item) => <Bubble key={item.itemId} item={item} />)
          )}
        </ScrollView>

        {snapshot.actions.length > 0 ? (
          <View style={styles.actions}>
            {snapshot.actions.map((action) => (
              <Pressable
                key={action.actionId}
                accessibilityRole="button"
                onPress={() => onAction?.(action)}
                style={styles.actionPressable}
              >
                <Text style={styles.actionPressableLabel}>{action.label}</Text>
              </Pressable>
            ))}
          </View>
        ) : null}

        <Composer onSubmit={onSubmit} />

        <Text style={styles.footer}>Private. Local. In your control.</Text>
      </View>

      <SettingsSheet
        visible={settingsOpen}
        snapshot={snapshot}
        typeSafeKeyStatus={typeSafeKeyStatus}
        onClose={() => setSettingsOpen(false)}
        onSetTypeSafeKey={async (value) => {
          if (!onSetTypeSafeKey) return;
          await onSetTypeSafeKey(value);
        }}
        onDeleteTypeSafeKey={async () => {
          if (!onDeleteTypeSafeKey) return;
          await onDeleteTypeSafeKey();
        }}
        onSetHostedProcessing={(enabled) => onSetHostedProcessing?.(enabled)}
        onRefreshHealth={() => onRefreshHealth?.()}
      />
    </View>
  );
}

function Bubble({ item }: { readonly item: FeedItemSnapshot }) {
  if (item.kind === "ask") {
    return (
      <View style={styles.userBubble} testID={`relay-feed-ask-${item.itemId}`}>
        <Text style={styles.userText} testID="relay-feed-ask-text">
          {item.summary}
        </Text>
      </View>
    );
  }
  if (item.kind === "task") {
    return (
      <View style={styles.assistantBubble} testID={`relay-feed-task-${item.itemId}`}>
        <Text style={styles.assistantName}>RELAY</Text>
        <Text style={styles.taskEyebrow}>Recommended task</Text>
        <Text style={styles.assistantText}>{item.summary}</Text>
      </View>
    );
  }
  if (item.kind === "memory") {
    return (
      <View style={styles.assistantBubble} testID={`relay-feed-memory-${item.itemId}`}>
        <Text style={styles.assistantName}>RELAY</Text>
        <Text style={styles.taskEyebrow}>Memory</Text>
        <Text style={styles.assistantText}>{item.summary}</Text>
      </View>
    );
  }
  return (
    <View style={styles.assistantBubble} testID={`relay-feed-answer-${item.itemId}`}>
      <Text style={styles.assistantName}>RELAY</Text>
      <Text style={styles.assistantText} testID="relay-feed-answer-text">
        {item.summary}
      </Text>
    </View>
  );
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
  actionPressable: {
    backgroundColor: colors.bgElevated,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  actionPressableLabel: {
    color: colors.text,
    fontSize: 13,
    fontWeight: "600",
  },
  footer: {
    color: colors.textDim,
    fontSize: 10,
    textAlign: "center",
    paddingBottom: 10,
    paddingTop: 8,
    borderTopWidth: 1,
    borderTopColor: colors.border,
  },
});
