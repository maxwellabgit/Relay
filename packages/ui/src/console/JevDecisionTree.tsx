import { StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";
import type { JevTreeNode, JevTreeView } from "./projectJevTree.js";

type Props = {
  readonly tree: JevTreeView;
};

export function JevDecisionTree({ tree }: Props) {
  return (
    <View style={styles.root}>
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.title}>Jev Decision Tree</Text>
          <Text style={styles.subtitle}>How Jev reached a bounded judgment for this request.</Text>
        </View>
        <Text style={styles.total}>
          {tree.totalMs != null ? `Total response time ${Math.round(tree.totalMs)} ms` : "Total response time —"}
        </Text>
      </View>

      <View style={styles.tree}>
        {tree.nodes.map((node, index) => (
          <TreeStep
            key={`${node.step}-${node.title}`}
            node={node}
            isLast={index === tree.nodes.length - 1}
          />
        ))}
      </View>

      <View style={styles.footer}>
        <Text style={tree.completed ? styles.footerOk : styles.footerWait}>
          {tree.completed
            ? `Completed${tree.totalMs != null ? ` in ${Math.round(tree.totalMs)} ms` : ""}`
            : "In progress · waiting on gate"}
        </Text>
      </View>
    </View>
  );
}

function TreeStep({ node, isLast }: { readonly node: JevTreeNode; readonly isLast: boolean }) {
  const terminal = node.status === "terminal";
  return (
    <View style={styles.stepBlock}>
      <View style={styles.stepRow}>
        <View style={[styles.badge, terminal ? styles.badgeTerminal : null]}>
          <Text style={[styles.badgeText, terminal ? styles.badgeTextTerminal : null]}>{node.step}</Text>
        </View>
        <View style={[styles.node, terminal ? styles.nodeTerminal : styles.nodeTaken]}>
          <View style={styles.nodeMain}>
            <Text style={[styles.nodeTitle, terminal ? styles.nodeTitleTerminal : null]}>
              {terminal ? `★ ${node.title}` : node.title}
            </Text>
            <Text style={styles.nodeDetail}>{node.detail}</Text>
          </View>
          <Text style={styles.nodeMs}>{formatMs(node.durationMs)}</Text>
        </View>
      </View>

      {node.branch ? (
        <View style={styles.branchRow}>
          <View style={styles.branchRail} />
          <View style={styles.branchBody}>
            <View style={styles.branchSplit}>
              <Text
                style={[
                  styles.branchLabel,
                  node.branch.exitTaken ? styles.branchIdle : styles.branchActive,
                ]}
              >
                {`${node.branch.continueLabel} ↓`}
              </Text>
              <View
                style={[
                  styles.branchExit,
                  node.branch.exitTaken ? styles.branchExitTaken : styles.branchExitIdle,
                ]}
              >
                <Text style={styles.branchArrow}>{`${node.branch.exitTaken ? "No" : "Alt"} →`}</Text>
                <Text style={styles.branchExitLabel}>{node.branch.exitLabel}</Text>
                <Text style={styles.branchMs}>{formatMs(node.branch.exitDurationMs)}</Text>
              </View>
            </View>
          </View>
        </View>
      ) : null}

      {!isLast ? (
        <View style={styles.connector}>
          <View style={styles.connectorLine} />
          <Text style={styles.connectorArrow}>↓</Text>
        </View>
      ) : null}
    </View>
  );
}

function formatMs(value: number | null): string {
  if (value == null) return "—";
  return `${Math.round(value)} ms`;
}

const styles = StyleSheet.create({
  root: {
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    backgroundColor: colors.bgPanel,
    padding: 14,
    gap: 12,
  },
  header: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    gap: 12,
    flexWrap: "wrap",
  },
  headerText: { flex: 1, gap: 2, minWidth: 180 },
  title: { color: colors.text, fontSize: 18, fontWeight: "700" },
  subtitle: { color: colors.textMuted, fontSize: 12 },
  total: { color: colors.cyan, fontSize: 12, fontWeight: "600" },
  tree: { gap: 0 },
  stepBlock: { gap: 0 },
  stepRow: { flexDirection: "row", alignItems: "stretch", gap: 10 },
  badge: {
    width: 28,
    height: 28,
    borderRadius: 14,
    backgroundColor: colors.blue,
    alignItems: "center",
    justifyContent: "center",
    marginTop: 8,
  },
  badgeTerminal: { backgroundColor: colors.warn },
  badgeText: { color: colors.text, fontSize: 12, fontWeight: "700" },
  badgeTextTerminal: { color: colors.bg },
  node: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
    borderWidth: 1,
    borderRadius: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
    backgroundColor: colors.bgElevated,
  },
  nodeTaken: { borderColor: colors.cyan },
  nodeTerminal: {
    borderColor: colors.warn,
    backgroundColor: "#241c0c",
  },
  nodeMain: { flex: 1, gap: 2 },
  nodeTitle: { color: colors.text, fontSize: 14, fontWeight: "700" },
  nodeTitleTerminal: { color: colors.warn },
  nodeDetail: { color: colors.textMuted, fontSize: 12 },
  nodeMs: { color: colors.textDim, fontSize: 12, fontFamily: "monospace" },
  branchRow: { flexDirection: "row", marginLeft: 13, marginTop: 4, marginBottom: 2 },
  branchRail: {
    width: 2,
    backgroundColor: colors.cyan,
    marginRight: 18,
  },
  branchBody: { flex: 1, paddingVertical: 4 },
  branchSplit: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: 10,
  },
  branchLabel: { fontSize: 11, fontWeight: "700", minWidth: 48 },
  branchActive: { color: colors.cyan },
  branchIdle: { color: colors.textDim },
  branchExit: {
    flex: 1,
    minWidth: 160,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderWidth: 1,
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 6,
    backgroundColor: colors.bg,
  },
  branchExitTaken: { borderColor: colors.warn },
  branchExitIdle: { borderColor: colors.borderStrong, opacity: 0.85 },
  branchArrow: { color: colors.textMuted, fontSize: 11, fontWeight: "600" },
  branchExitLabel: { color: colors.text, fontSize: 12, flex: 1 },
  branchMs: { color: colors.textDim, fontSize: 11, fontFamily: "monospace" },
  connector: { marginLeft: 26, paddingVertical: 2, alignItems: "flex-start" },
  connectorLine: { width: 2, height: 10, backgroundColor: colors.cyan },
  connectorArrow: { color: colors.cyan, fontSize: 12, marginLeft: -4 },
  footer: {
    borderTopWidth: 1,
    borderTopColor: colors.border,
    paddingTop: 10,
  },
  footerOk: { color: colors.ok, fontSize: 12, fontWeight: "600" },
  footerWait: { color: colors.warn, fontSize: 12, fontWeight: "600" },
});
