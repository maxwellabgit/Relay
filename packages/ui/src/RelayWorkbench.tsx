import type { RelaySnapshot } from "@relay/contracts";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { DeveloperConsole } from "./console/DeveloperConsole.js";
import { PhoneShell } from "./phone/PhoneShell.js";
import { colors } from "./theme/colors.js";

export type RelayWorkbenchProps = {
  readonly snapshot: RelaySnapshot;
  readonly onListenChange: (enabled: boolean) => void;
  readonly onSubmit: (text: string) => void;
  readonly onRemember?: (token: string) => void;
  readonly onCaptureBirthday?: (personKey: string, date: string, confirmed: boolean) => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
  readonly showDeveloperPanel?: boolean;
  readonly traceLines?: string[];
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
};

const WIDE_BREAKPOINT = 960;

export function RelayWorkbench({
  snapshot,
  onListenChange,
  onSubmit,
  onRemember,
  onCaptureBirthday,
  onStartSession,
  onEndSession,
  showDeveloperPanel,
  traceLines,
  onReplayFixture,
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
          {...(onRemember !== undefined ? { onRemember } : {})}
          {...(onCaptureBirthday !== undefined ? { onCaptureBirthday } : {})}
        />
      </View>
      {showDev ? (
        <DeveloperConsole
          snapshot={snapshot}
          {...(traceLines !== undefined ? { traceLines } : {})}
          {...(onReplayFixture !== undefined ? { onReplayFixture } : {})}
          {...(onStartSession !== undefined ? { onStartSession } : {})}
          {...(onEndSession !== undefined ? { onEndSession } : {})}
          onOpenLog={() => {
            const path = snapshot.runtime.logPath;
            if (path && typeof navigator !== "undefined" && navigator.clipboard) {
              void navigator.clipboard.writeText(path);
            }
          }}
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
