import type { ActionCard, FeedItemSnapshot, RelaySnapshot, StatusChipState } from "@relay/contracts";
import { useEffect, useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import {
  AmbientRecommendationCard,
  type AmbientFeedback,
} from "../assistant/AmbientRecommendationCard.js";
import { Composer } from "../assistant/Composer.js";
import { LibrarySheet } from "../assistant/LibrarySheet.js";
import { SettingsSheet } from "../assistant/SettingsSheet.js";
import { colors } from "../theme/colors.js";
import { usePrefersReducedMotion } from "../theme/reducedMotion.js";
import { radius, space, touchTarget, typeScale } from "../theme/tokens.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onAction?: (action: ActionCard) => void;
  readonly onAcceptAmbient?: (recommendationId: string) => void;
  readonly onDismissAmbient?: (recommendationId: string) => void;
  readonly onFeedbackAmbient?: (recommendationId: string, feedback: AmbientFeedback) => void;
  readonly onCancelActive?: () => void;
  readonly typeSafeKeyStatus?: "present" | "disabled" | "unknown";
  readonly onSetTypeSafeKey?: (value: string) => Promise<void>;
  readonly onDeleteTypeSafeKey?: () => Promise<void>;
  readonly onSetHostedProcessing?: (enabled: boolean) => void;
  readonly onRefreshHealth?: () => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onActivateReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onPauseReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onRollbackReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  /** When false, render full-bleed product surface (Expo/mobile). Default true for desktop workbench. */
  readonly showBezel?: boolean;
};

export function PhoneShell({
  snapshot,
  onListenChange,
  onSubmit,
  onAction,
  onAcceptAmbient,
  onDismissAmbient,
  onFeedbackAmbient,
  onCancelActive,
  typeSafeKeyStatus = "unknown",
  onSetTypeSafeKey,
  onDeleteTypeSafeKey,
  onSetHostedProcessing,
  onRefreshHealth,
  onApproveCandidate,
  onActivateReflex,
  onPauseReflex,
  onRollbackReflex,
  showBezel = true,
}: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [listenElapsedSec, setListenElapsedSec] = useState(0);
  const listenStartedAt = useRef<number | null>(null);
  const threadRef = useRef<ScrollView>(null);
  const reducedMotion = usePrefersReducedMotion();

  useEffect(() => {
    if (!snapshot.listening) {
      listenStartedAt.current = null;
      setListenElapsedSec(0);
      return;
    }
    if (listenStartedAt.current == null) {
      listenStartedAt.current = Date.now();
    }
    const timer = setInterval(() => {
      const started = listenStartedAt.current;
      if (started != null) {
        setListenElapsedSec(Math.floor((Date.now() - started) / 1000));
      }
    }, reducedMotion ? 1000 : 500);
    return () => clearInterval(timer);
  }, [snapshot.listening, reducedMotion]);

  const health = aggregateHealth(snapshot.status, snapshot.providerHealth);
  const ambientActions = snapshot.actions.filter((a) => a.kind === "ambient_recommendation");
  const otherActions = snapshot.actions.filter((a) => a.kind !== "ambient_recommendation");
  const waitingLabel =
    snapshot.waits.length > 0
      ? `Waiting · ${snapshot.waits[0]?.waitKind ?? "work"}`
      : snapshot.queueDepth > 0
        ? `Working · ${snapshot.queueDepth} queued`
        : null;

  const screen = (
    <View style={styles.screen}>
      <View style={styles.header}>
        <View style={styles.brandRow}>
          <Text style={styles.brand} accessibilityRole="header">
            RELAY
          </Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={`Health ${health.label}`}
            onPress={() => setSettingsOpen(true)}
            style={styles.healthHit}
            hitSlop={8}
          >
            <View
              style={[
                styles.healthDot,
                health.level === "ok"
                  ? styles.healthOk
                  : health.level === "warn"
                    ? styles.healthWarn
                    : styles.healthBad,
              ]}
            />
          </Pressable>
        </View>
        <View style={styles.headerActions}>
          {snapshot.listening ? (
            <Text
              style={styles.stopwatch}
              accessibilityLabel={`Listening for ${formatElapsed(listenElapsedSec)}`}
            >
              {formatElapsed(listenElapsedSec)}
            </Text>
          ) : null}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Open library"
            onPress={() => setLibraryOpen(true)}
            hitSlop={8}
            style={styles.headerBtn}
          >
            <Text style={styles.headerBtnLabel}>Library</Text>
          </Pressable>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Open settings"
            onPress={() => setSettingsOpen(true)}
            hitSlop={8}
            style={styles.headerBtn}
          >
            <Text style={styles.headerBtnLabel}>Settings</Text>
          </Pressable>
        </View>
      </View>

      <View style={styles.listenRow}>
        <Pressable
          accessibilityRole="switch"
          accessibilityState={{ checked: snapshot.listening }}
          accessibilityLabel="Listening"
          onPress={() => onListenChange(!snapshot.listening)}
          style={[styles.listenBtn, snapshot.listening ? styles.listenOn : styles.listenOff]}
        >
          <View style={[styles.dot, snapshot.listening ? styles.dotOn : styles.dotOff]} />
          <Text style={styles.listenLabel}>{snapshot.listening ? "Listening" : "Listen"}</Text>
        </Pressable>
        {snapshot.listening && !reducedMotion ? (
          <View style={styles.wave} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
            {[10, 18, 28, 16, 24].map((height, index) => (
              <View key={index} style={[styles.waveBar, { height }]} />
            ))}
          </View>
        ) : null}
      </View>

      <ScrollView
        ref={threadRef}
        style={styles.thread}
        contentContainerStyle={styles.threadContent}
        onContentSizeChange={() => {
          threadRef.current?.scrollToEnd({ animated: !reducedMotion });
        }}
      >
        {snapshot.feedItems.length === 0 ? (
          <Text style={styles.empty}>Ask RELAY anything. Listening stays quiet until something useful appears.</Text>
        ) : (
          snapshot.feedItems.map((item) => <Bubble key={item.itemId} item={item} />)
        )}
      </ScrollView>

      {ambientActions.length > 0 ? (
        <View style={styles.ambientStack}>
          {ambientActions.map((action) => (
            <AmbientRecommendationCard
              key={action.actionId}
              action={action}
              onAccept={(id) => onAcceptAmbient?.(id)}
              onDismiss={(id) => onDismissAmbient?.(id)}
              onFeedback={(id, feedback) => onFeedbackAmbient?.(id, feedback)}
            />
          ))}
        </View>
      ) : null}

      {otherActions.length > 0 ? (
        <View style={styles.actions}>
          {otherActions.map((action) => (
            <Pressable
              key={action.actionId}
              accessibilityRole="button"
              accessibilityLabel={action.label}
              onPress={() => onAction?.(action)}
              style={styles.actionPressable}
            >
              <Text style={styles.actionPressableLabel}>{action.label}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}

      <Composer
        onSubmit={onSubmit}
        listening={snapshot.listening}
        waitingLabel={waitingLabel}
        {...(onCancelActive ? { onCancel: onCancelActive } : {})}
      />

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
      <LibrarySheet
        open={libraryOpen}
        snapshot={snapshot}
        onClose={() => setLibraryOpen(false)}
        {...(onApproveCandidate ? { onApproveCandidate } : {})}
        {...(onActivateReflex ? { onActivateReflex } : {})}
        {...(onPauseReflex ? { onPauseReflex } : {})}
        {...(onRollbackReflex ? { onRollbackReflex } : {})}
      />
    </View>
  );

  if (!showBezel) {
    return <View style={styles.fullBleed}>{screen}</View>;
  }
  return <View style={styles.bezel}>{screen}</View>;
}

function aggregateHealth(
  status: readonly StatusChipState[],
  providers: RelaySnapshot["providerHealth"],
): { level: "ok" | "warn" | "bad"; label: string } {
  const badStatus = status.find((s) => !s.ok);
  const badProvider = providers.find((p) => !p.ok);
  if (badStatus || badProvider) {
    return {
      level: "bad",
      label: badStatus?.label ?? badProvider?.status ?? "degraded",
    };
  }
  if (providers.length === 0 && status.length === 0) {
    return { level: "warn", label: "unknown" };
  }
  return { level: "ok", label: "healthy" };
}

function formatElapsed(totalSec: number): string {
  const m = Math.floor(totalSec / 60)
    .toString()
    .padStart(2, "0");
  const s = (totalSec % 60).toString().padStart(2, "0");
  return `${m}:${s}`;
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
        <Text style={styles.taskEyebrow}>Note</Text>
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
  fullBleed: {
    flex: 1,
    backgroundColor: colors.phone,
  },
  bezel: {
    width: 390,
    maxWidth: "100%",
    maxHeight: "100%",
    flex: 1,
    borderRadius: radius.xl,
    padding: 10,
    backgroundColor: "#05080e",
    borderWidth: 1,
    borderColor: "#1c2838",
  },
  screen: {
    flex: 1,
    borderRadius: radius.xl,
    backgroundColor: colors.phone,
    overflow: "hidden",
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: space.md,
    paddingTop: space.md,
    paddingBottom: space.xs,
    minHeight: touchTarget,
  },
  headerActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: space.xs,
  },
  brandRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: space.xs,
  },
  brand: {
    color: colors.text,
    fontSize: typeScale.md,
    fontWeight: "800",
    letterSpacing: 1.4,
  },
  healthHit: {
    minWidth: touchTarget / 2,
    minHeight: touchTarget / 2,
    alignItems: "center",
    justifyContent: "center",
  },
  healthDot: {
    width: 10,
    height: 10,
    borderRadius: 5,
  },
  healthOk: { backgroundColor: colors.ok },
  healthWarn: { backgroundColor: colors.warn },
  healthBad: { backgroundColor: colors.danger },
  stopwatch: {
    color: colors.textMuted,
    fontSize: typeScale.xs,
    fontVariant: ["tabular-nums"],
    minWidth: 40,
  },
  headerBtn: {
    minHeight: touchTarget,
    justifyContent: "center",
    paddingHorizontal: space.xs,
  },
  headerBtnLabel: {
    color: colors.textMuted,
    fontSize: typeScale.sm,
    fontWeight: "600",
  },
  listenRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: space.sm,
    paddingHorizontal: space.md,
    paddingBottom: space.sm,
  },
  wave: {
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
    height: 28,
  },
  waveBar: {
    width: 3,
    borderRadius: 2,
    backgroundColor: colors.accent,
    opacity: 0.7,
  },
  listenBtn: {
    flexDirection: "row",
    alignItems: "center",
    gap: space.xs,
    borderRadius: radius.lg,
    minHeight: touchTarget,
    paddingVertical: space.xs,
    paddingHorizontal: space.md,
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
    fontSize: typeScale.sm,
  },
  thread: { flex: 1 },
  threadContent: {
    paddingHorizontal: space.sm,
    paddingBottom: space.xs,
    gap: space.xs,
  },
  empty: {
    color: colors.textMuted,
    fontSize: typeScale.sm,
    lineHeight: 20,
    textAlign: "center",
    paddingHorizontal: space.lg,
    paddingTop: space.xl,
  },
  ambientStack: {
    paddingHorizontal: space.sm,
    paddingBottom: space.xs,
    gap: space.xs,
  },
  actions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: space.xs,
    paddingHorizontal: space.sm,
    paddingBottom: space.xs,
  },
  actionPressable: {
    minHeight: touchTarget,
    justifyContent: "center",
    backgroundColor: colors.bgElevated,
    borderRadius: radius.sm,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: space.md,
  },
  actionPressableLabel: {
    color: colors.text,
    fontWeight: "600",
    fontSize: typeScale.sm,
  },
  userBubble: {
    alignSelf: "flex-end",
    backgroundColor: colors.userBubble,
    borderRadius: radius.md,
    paddingHorizontal: space.sm,
    paddingVertical: space.xs,
    maxWidth: "85%",
  },
  userText: {
    color: colors.text,
    fontSize: typeScale.sm,
  },
  assistantBubble: {
    alignSelf: "flex-start",
    backgroundColor: colors.bgElevated,
    borderRadius: radius.md,
    paddingHorizontal: space.sm,
    paddingVertical: space.xs,
    maxWidth: "90%",
    gap: 2,
  },
  assistantName: {
    color: colors.accent,
    fontSize: typeScale.xs,
    fontWeight: "700",
  },
  taskEyebrow: {
    color: colors.textDim,
    fontSize: typeScale.xs,
  },
  assistantText: {
    color: colors.text,
    fontSize: typeScale.sm,
    lineHeight: 20,
  },
});
