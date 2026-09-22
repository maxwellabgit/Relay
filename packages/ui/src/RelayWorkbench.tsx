import type { ActionCard, RelaySnapshot } from "@relay/contracts";
import type { ProductSurface } from "./assistant/product-surface.js";
import { useState } from "react";
import { Modal, Pressable, StyleSheet, Text, useWindowDimensions, View } from "react-native";
import { SafeAreaProvider } from "react-native-safe-area-context";
import type { AmbientFeedback } from "./assistant/AmbientRecommendationCard.js";
import { DeveloperConsole } from "./console/DeveloperConsole.js";
import { PhoneShell } from "./phone/PhoneShell.js";
import { colors } from "./theme/colors.js";
import { radius, space, touchTarget, typeScale } from "./theme/tokens.js";

export type RelayWorkbenchProps = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onAction?: (action: ActionCard) => void;
  readonly onAcceptAmbient?: (recommendationId: string) => void;
  readonly onDismissAmbient?: (recommendationId: string) => void;
  readonly onFeedbackAmbient?: (recommendationId: string, feedback: AmbientFeedback) => void;
  readonly onCancelActive?: () => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
  readonly showDeveloperPanel?: boolean;
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onOpenLog?: () => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onActivateReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onPauseReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onRollbackReflex?: (reflexId: string, version: number, stateVersion: number) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
  readonly typeSafeKeyStatus?: "present" | "disabled" | "unknown";
  readonly onSetTypeSafeKey?: (value: string) => Promise<void>;
  readonly onDeleteTypeSafeKey?: () => Promise<void>;
  readonly onSetHostedProcessing?: (enabled: boolean) => void;
  readonly onGrantJevDisclosure?: () => void;
  readonly onRevokeJevDisclosure?: (grantId: string) => void;
  readonly onRefreshHealth?: () => void;
  readonly onExportDiagnostics?: () => string;
  readonly busy?: boolean;
  readonly notice?: string | null;
  readonly surface?: ProductSurface;
  /** When false, product surface is full-bleed (mobile). Default true. */
  readonly showBezel?: boolean;
};

const WIDE = 1180;
const MEDIUM = 800;

export function RelayWorkbench({
  snapshot,
  onListenChange,
  onSubmit,
  onAction,
  onAcceptAmbient,
  onDismissAmbient,
  onFeedbackAmbient,
  onCancelActive,
  onStartSession,
  onEndSession,
  showDeveloperPanel,
  onReplayFixture,
  onOpenLog,
  onApproveCandidate,
  onActivateReflex,
  onPauseReflex,
  onRollbackReflex,
  onRejectCandidate,
  onSnoozeCandidate,
  typeSafeKeyStatus,
  onSetTypeSafeKey,
  onDeleteTypeSafeKey,
  onSetHostedProcessing,
  onGrantJevDisclosure,
  onRevokeJevDisclosure,
  onRefreshHealth,
  onExportDiagnostics,
  busy = false,
  notice = null,
  surface = "ready",
  showBezel = true,
}: RelayWorkbenchProps) {
  const { width } = useWindowDimensions();
  const [devOpen, setDevOpen] = useState(false);

  const layout: "wide" | "medium" | "narrow" =
    width >= WIDE ? "wide" : width >= MEDIUM ? "medium" : "narrow";
  const showDev = showDeveloperPanel === true;
  const showDevInline = showDev && layout === "wide";
  const showDevDrawer = showDev && !showDevInline;

  const phone = (
    <PhoneShell
      snapshot={snapshot}
      onListenChange={onListenChange}
      onSubmit={onSubmit}
      showBezel={showBezel && layout !== "narrow"}
      {...(onAction !== undefined ? { onAction } : {})}
      {...(onAcceptAmbient !== undefined ? { onAcceptAmbient } : {})}
      {...(onDismissAmbient !== undefined ? { onDismissAmbient } : {})}
      {...(onFeedbackAmbient !== undefined ? { onFeedbackAmbient } : {})}
      {...(onCancelActive !== undefined ? { onCancelActive } : {})}
      {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
      {...(onActivateReflex !== undefined ? { onActivateReflex } : {})}
      {...(onPauseReflex !== undefined ? { onPauseReflex } : {})}
      {...(onRollbackReflex !== undefined ? { onRollbackReflex } : {})}
      {...(typeSafeKeyStatus !== undefined ? { typeSafeKeyStatus } : {})}
      {...(onSetTypeSafeKey !== undefined ? { onSetTypeSafeKey } : {})}
      {...(onDeleteTypeSafeKey !== undefined ? { onDeleteTypeSafeKey } : {})}
      {...(onSetHostedProcessing !== undefined ? { onSetHostedProcessing } : {})}
      {...(onGrantJevDisclosure !== undefined ? { onGrantJevDisclosure } : {})}
      {...(onRevokeJevDisclosure !== undefined ? { onRevokeJevDisclosure } : {})}
      {...(onRefreshHealth !== undefined ? { onRefreshHealth } : {})}
      {...(onExportDiagnostics !== undefined ? { onExportDiagnostics } : {})}
      busy={busy}
      notice={notice}
      surface={surface}
    />
  );

  const consoleProps = {
    snapshot,
    ...(onReplayFixture !== undefined ? { onReplayFixture } : {}),
    ...(onStartSession !== undefined ? { onStartSession } : {}),
    ...(onEndSession !== undefined ? { onEndSession } : {}),
    ...(onOpenLog !== undefined ? { onOpenLog } : {}),
    ...(onApproveCandidate !== undefined ? { onApproveCandidate } : {}),
    ...(onRejectCandidate !== undefined ? { onRejectCandidate } : {}),
    ...(onSnoozeCandidate !== undefined ? { onSnoozeCandidate } : {}),
  };

  return (
    <SafeAreaProvider>
    <View style={styles.root}>
      <View
        style={[
          styles.stage,
          layout === "wide" ? styles.stageWide : null,
          layout === "medium" ? styles.stageMedium : null,
          layout === "narrow" ? styles.stageNarrow : null,
        ]}
      >
        {phone}
        {showDevDrawer ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Open developer console"
            onPress={() => setDevOpen(true)}
            style={styles.devFab}
          >
            <Text style={styles.devFabLabel}>Dev</Text>
          </Pressable>
        ) : null}
      </View>
      {showDevInline ? (
        <View style={styles.consolePane}>
          <DeveloperConsole {...consoleProps} />
        </View>
      ) : null}
      {showDevDrawer ? (
        <Modal
          visible={devOpen}
          animationType="slide"
          onRequestClose={() => setDevOpen(false)}
          transparent
        >
          <View style={styles.drawerBackdrop}>
            <View style={styles.drawer}>
              <View style={styles.drawerHeader}>
                <Text style={styles.drawerTitle}>Developer console</Text>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel="Close developer console"
                  onPress={() => setDevOpen(false)}
                  style={styles.drawerClose}
                >
                  <Text style={styles.drawerCloseLabel}>Close</Text>
                </Pressable>
              </View>
              <DeveloperConsole {...consoleProps} />
            </View>
          </View>
        </Modal>
      ) : null}
    </View>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
    flexDirection: "row",
    backgroundColor: colors.bg,
  },
  stage: {
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: space.md,
  },
  stageWide: {
    width: 420,
    paddingLeft: space.md,
    paddingRight: space.xs,
  },
  stageMedium: {
    flex: 1,
    paddingHorizontal: space.md,
  },
  stageNarrow: {
    flex: 1,
    paddingHorizontal: 0,
    paddingVertical: 0,
  },
  consolePane: {
    flex: 1,
    minWidth: 0,
  },
  devFab: {
    position: "absolute",
    left: space.xs,
    top: "42%",
    zIndex: 5,
    minHeight: touchTarget,
    minWidth: touchTarget,
    borderRadius: radius.md,
    backgroundColor: colors.bgElevated,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: space.sm,
  },
  devFabLabel: {
    color: colors.textMuted,
    fontWeight: "700",
    fontSize: typeScale.xs,
  },
  drawerBackdrop: {
    flex: 1,
    backgroundColor: "rgba(0,0,0,0.45)",
    justifyContent: "flex-end",
  },
  drawer: {
    height: "88%",
    backgroundColor: colors.console,
    borderTopLeftRadius: radius.lg,
    borderTopRightRadius: radius.lg,
    overflow: "hidden",
  },
  drawerHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  drawerTitle: {
    color: colors.text,
    fontWeight: "700",
    fontSize: typeScale.sm,
  },
  drawerClose: {
    minHeight: touchTarget,
    justifyContent: "center",
    paddingHorizontal: space.xs,
  },
  drawerCloseLabel: {
    color: colors.accent,
    fontWeight: "600",
  },
});
