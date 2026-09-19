import type { RelaySnapshot } from "@relay/contracts";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { Composer } from "./assistant/Composer";
import { Feed } from "./assistant/Feed";
import { ListenToggle } from "./assistant/ListenToggle";
import { PendingCard } from "./assistant/PendingCard";
import { StatusChips } from "./assistant/StatusChips";
import { DeveloperPanel } from "./developer/DeveloperPanel";
import { colors } from "./theme/colors";

export type RelayWorkbenchProps = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly showDeveloperPanel?: boolean;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
};

const WIDE_BREAKPOINT = 960;

export function RelayWorkbench({
  snapshot,
  onListenChange,
  onSubmit,
  showDeveloperPanel,
  traceLines,
  onReplayFixture,
}: RelayWorkbenchProps) {
  const { width } = useWindowDimensions();
  const showDev = showDeveloperPanel ?? width >= WIDE_BREAKPOINT;

  return (
    <View style={styles.root}>
      <View style={styles.assistant}>
        <ListenToggle listening={snapshot.listening} onChange={onListenChange} />
        <StatusChips status={snapshot.status} />
        <PendingCard approvals={snapshot.approvals} />
        <Feed items={snapshot.feedItems} />
        <Composer onSubmit={onSubmit} />
      </View>
      {showDev ? (
        <DeveloperPanel
          snapshot={snapshot}
          {...(traceLines !== undefined ? { traceLines } : {})}
          {...(onReplayFixture !== undefined ? { onReplayFixture } : {})}
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
  assistant: {
    flex: 1,
    maxWidth: 560,
    alignSelf: "stretch",
    backgroundColor: colors.bg,
    borderRightWidth: 1,
    borderRightColor: colors.border,
  },
});
