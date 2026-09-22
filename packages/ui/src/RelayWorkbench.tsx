import type { ActionCard, RelaySnapshot } from "@relay/contracts";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { DeveloperConsole } from "./console/DeveloperConsole.js";
import { PhoneShell } from "./phone/PhoneShell.js";
import { colors } from "./theme/colors.js";

export type RelayWorkbenchProps = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onAction?: (action: ActionCard) => void;
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
  readonly onRefreshHealth?: () => void;
};

const WIDE_BREAKPOINT = 960;

export function RelayWorkbench({
  snapshot,
  onListenChange,
  onSubmit,
  onAction,
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
  onRefreshHealth,
}: RelayWorkbenchProps) {
  const { width } = useWindowDimensions();
  const showDev = showDeveloperPanel ?? width >= WIDE_BREAKPOINT;

  return (
    <View style={styles.root}>
      <View style={styles.stage}>
        <PhoneShell
          snapshot={snapshot}
          onListenChange={onListenChange}
          onSubmit={onSubmit}
          {...(onAction !== undefined ? { onAction } : {})}
          {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
          {...(onActivateReflex !== undefined ? { onActivateReflex } : {})}
          {...(onPauseReflex !== undefined ? { onPauseReflex } : {})}
          {...(onRollbackReflex !== undefined ? { onRollbackReflex } : {})}
          {...(typeSafeKeyStatus !== undefined ? { typeSafeKeyStatus } : {})}
          {...(onSetTypeSafeKey !== undefined ? { onSetTypeSafeKey } : {})}
          {...(onDeleteTypeSafeKey !== undefined ? { onDeleteTypeSafeKey } : {})}
          {...(onSetHostedProcessing !== undefined ? { onSetHostedProcessing } : {})}
          {...(onRefreshHealth !== undefined ? { onRefreshHealth } : {})}
        />
      </View>
      {showDev ? (
        <DeveloperConsole
          snapshot={snapshot}
          {...(onReplayFixture !== undefined ? { onReplayFixture } : {})}
          {...(onStartSession !== undefined ? { onStartSession } : {})}
          {...(onEndSession !== undefined ? { onEndSession } : {})}
          {...(onOpenLog !== undefined ? { onOpenLog } : {})}
          {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
          {...(onRejectCandidate !== undefined ? { onRejectCandidate } : {})}
          {...(onSnoozeCandidate !== undefined ? { onSnoozeCandidate } : {})}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
    flexDirection: "row",
    backgroundColor: colors.bg,
  },
  stage: {
    width: 430,
    paddingVertical: 18,
    paddingLeft: 18,
    paddingRight: 8,
    alignItems: "center",
    justifyContent: "center",
  },
});
