import type { ProjectCaseView, VerifyItemView } from "@relay/contracts";
import { Pressable, ScrollView, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly cases: readonly ProjectCaseView[];
  readonly activityIds: readonly string[];
  readonly onRename?: (projectCaseId: string, alias: string, expectedVersion: number) => void;
};

export function CasesPane({ cases, activityIds, onRename }: Props) {
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 12 }}>
      {cases.length === 0 ? <Text style={{ color: colors.textMuted }}>No Cases yet.</Text> : null}
      {cases.map((item) => {
        const active = activityIds.includes(item.projectCaseId);
        return (
          <View
            key={item.projectCaseId}
            style={{
              borderWidth: 1,
              borderColor: active ? colors.accent : colors.border,
              borderRadius: 12,
              padding: 12,
              backgroundColor: colors.bgElevated,
            }}
          >
            <Text style={{ color: colors.text, fontWeight: "700" }}>{item.alias}</Text>
            <Text style={{ color: colors.textMuted, marginTop: 4 }}>{item.intent}</Text>
            <Text style={{ color: colors.textMuted, marginTop: 8 }}>
              {`${item.entryCount} entries · ${item.referenceCount} links · v${item.version}`}
            </Text>
            {item.pendingVerify > 0 ? (
              <Text style={{ color: colors.warn, marginTop: 6 }}>{`${item.pendingVerify} in Verify`}</Text>
            ) : null}
            {onRename ? (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`Rename ${item.alias}`}
                onPress={() => onRename(item.projectCaseId, `${item.alias} renamed`, item.version)}
                style={{ marginTop: 10, minHeight: 44, justifyContent: "center" }}
              >
                <Text style={{ color: colors.accent }}>Rename</Text>
              </Pressable>
            ) : null}
          </View>
        );
      })}
    </ScrollView>
  );
}

export function VerifyPane({
  items,
  onDecide,
}: {
  readonly items: readonly VerifyItemView[];
  readonly onDecide?: (verifyId: string, decision: "accept" | "dismiss" | "correct", correction?: string) => void;
}) {
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 12 }}>
      {items.filter((item) => item.disposition === "pending").length === 0 ? (
        <Text style={{ color: colors.textMuted }}>Nothing waiting.</Text>
      ) : null}
      {items.map((item) => (
        <View
          key={item.verifyId}
          style={{
            borderWidth: 1,
            borderColor: item.evidenceStatus === "Contradicted" ? colors.warn : colors.border,
            borderRadius: 12,
            padding: 12,
          }}
        >
          <Text style={{ color: colors.text, fontWeight: "700" }}>{item.evidenceStatus}</Text>
          <Text style={{ color: colors.text, marginTop: 4 }}>{item.reason}</Text>
          <Text style={{ color: colors.textMuted, marginTop: 4 }}>{item.proposedChange}</Text>
          <Text style={{ color: colors.textMuted, marginTop: 4 }}>{item.disposition}</Text>
          {onDecide && item.disposition === "pending" ? (
            <View style={{ flexDirection: "row", gap: 12, marginTop: 10 }}>
              <Pressable accessibilityRole="button" accessibilityLabel="Accept" onPress={() => onDecide(item.verifyId, "accept")}>
                <Text style={{ color: colors.accent }}>Accept</Text>
              </Pressable>
              <Pressable accessibilityRole="button" accessibilityLabel="Dismiss" onPress={() => onDecide(item.verifyId, "dismiss")}>
                <Text style={{ color: colors.text }}>Dismiss</Text>
              </Pressable>
            </View>
          ) : null}
        </View>
      ))}
    </ScrollView>
  );
}
