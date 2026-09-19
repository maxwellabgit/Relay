import type { ApprovalSnapshot } from "@relay/contracts";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly approvals: readonly ApprovalSnapshot[];
  readonly onApprove?: (operationId: string) => void;
  readonly onReject?: (operationId: string) => void;
};

export function PendingCard({ approvals, onApprove, onReject }: Props) {
  if (approvals.length === 0) return null;

  return (
    <View style={styles.wrap}>
      {approvals.map((approval) => (
        <View key={approval.operationId} style={styles.card}>
          <Text style={styles.title}>Pending approval</Text>
          <Text style={styles.summary}>{approval.summary}</Text>
          <Text style={styles.meta}>
            {approval.action.actionId} · v{approval.caseVersion}
          </Text>
          <View style={styles.actions}>
            <Pressable
              style={[styles.btn, styles.reject]}
              onPress={() => onReject?.(approval.operationId)}
            >
              <Text style={styles.btnText}>Reject</Text>
            </Pressable>
            <Pressable
              style={[styles.btn, styles.approve]}
              onPress={() => onApprove?.(approval.operationId)}
            >
              <Text style={styles.btnText}>Approve</Text>
            </Pressable>
          </View>
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    paddingHorizontal: 16,
    gap: 8,
    marginBottom: 8,
  },
  card: {
    backgroundColor: colors.bgElevated,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.warn,
    padding: 12,
  },
  title: {
    color: colors.warn,
    fontSize: 12,
    fontWeight: "700",
    marginBottom: 4,
    textTransform: "uppercase",
    letterSpacing: 0.5,
  },
  summary: {
    color: colors.text,
    fontSize: 14,
    marginBottom: 6,
  },
  meta: {
    color: colors.textMuted,
    fontSize: 11,
    marginBottom: 10,
  },
  actions: {
    flexDirection: "row",
    gap: 8,
    justifyContent: "flex-end",
  },
  btn: {
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  reject: {
    backgroundColor: colors.danger,
  },
  approve: {
    backgroundColor: colors.ok,
  },
  btnText: {
    color: colors.text,
    fontWeight: "700",
    fontSize: 13,
  },
});
