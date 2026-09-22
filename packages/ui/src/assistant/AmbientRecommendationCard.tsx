import type { ActionCard } from "@relay/contracts";
import { useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";
import { radius, space, touchTarget, typeScale } from "../theme/tokens.js";

export type AmbientFeedback = "not_useful" | "never_for_project" | "why";

type Props = {
  readonly action: ActionCard;
  readonly onAccept: (recommendationId: string) => void;
  readonly onDismiss: (recommendationId: string) => void;
  readonly onFeedback: (recommendationId: string, feedback: AmbientFeedback) => void;
};

/**
 * Quiet ambient recommendation card — primary + dismiss + overflow feedback.
 * Never surfaces internal terms (Noul, gate, signature, threshold).
 */
export function AmbientRecommendationCard({ action, onAccept, onDismiss, onFeedback }: Props) {
  const [menuOpen, setMenuOpen] = useState(false);
  const recommendationId = action.recommendationId ?? "";
  if (!recommendationId) return null;

  const primaryLabel =
    action.label ||
    (action.primary === "verify"
      ? "Verify"
      : action.primary === "create_task"
        ? "Create task"
        : action.primary === "review"
          ? "Review"
          : "Save");

  return (
    <View
      style={[styles.card, action.quiet ? styles.cardQuiet : null]}
      testID={`relay-ambient-${recommendationId}`}
    >
      <Text style={styles.title} accessibilityRole="header">
        {action.title ?? primaryLabel}
      </Text>
      {action.reason ? <Text style={styles.reason}>{action.reason}</Text> : null}
      {typeof action.evidenceCount === "number" && action.evidenceCount > 0 ? (
        <Text style={styles.chip}>{`${action.evidenceCount} source${action.evidenceCount === 1 ? "" : "s"}`}</Text>
      ) : null}

      <View style={styles.row}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={primaryLabel}
          testID={`relay-ambient-accept-${recommendationId}`}
          onPress={() => onAccept(recommendationId)}
          style={styles.primary}
        >
          <Text style={styles.primaryLabel}>{primaryLabel}</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Dismiss recommendation"
          testID={`relay-ambient-dismiss-${recommendationId}`}
          onPress={() => onDismiss(recommendationId)}
          style={styles.secondary}
        >
          <Text style={styles.secondaryLabel}>Dismiss</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="More recommendation options"
          testID={`relay-ambient-more-${recommendationId}`}
          onPress={() => setMenuOpen((open) => !open)}
          style={styles.more}
        >
          <Text style={styles.secondaryLabel}>⋯</Text>
        </Pressable>
      </View>

      {menuOpen ? (
        <View style={styles.menu} accessibilityRole="menu">
          {(
            [
              ["not_useful", "Not useful"],
              ["never_for_project", "Never for this project"],
              ["why", "Why this appeared"],
            ] as const
          ).map(([feedback, label]) => (
            <Pressable
              key={feedback}
              accessibilityRole="menuitem"
              accessibilityLabel={label}
              onPress={() => {
                setMenuOpen(false);
                onFeedback(recommendationId, feedback);
              }}
              style={styles.menuItem}
            >
              <Text style={styles.menuLabel}>{label}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: colors.bgElevated,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border,
    padding: space.md,
    gap: space.xs,
  },
  cardQuiet: {
    borderColor: colors.border,
    opacity: 0.95,
  },
  title: {
    color: colors.text,
    fontSize: typeScale.sm,
    fontWeight: "700",
  },
  reason: {
    color: colors.textMuted,
    fontSize: typeScale.xs,
    lineHeight: 18,
  },
  chip: {
    alignSelf: "flex-start",
    color: colors.accent,
    fontSize: typeScale.xs,
    backgroundColor: colors.accentSoft,
    paddingHorizontal: space.xs,
    paddingVertical: 2,
    borderRadius: radius.sm,
    overflow: "hidden",
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: space.xs,
    marginTop: space.xs,
  },
  primary: {
    minHeight: touchTarget,
    justifyContent: "center",
    backgroundColor: colors.accent,
    borderRadius: radius.sm,
    paddingHorizontal: space.md,
    paddingVertical: space.xs,
  },
  primaryLabel: {
    color: colors.text,
    fontWeight: "700",
    fontSize: typeScale.sm,
  },
  secondary: {
    minHeight: touchTarget,
    justifyContent: "center",
    borderRadius: radius.sm,
    borderWidth: 1,
    borderColor: colors.borderStrong,
    paddingHorizontal: space.md,
    paddingVertical: space.xs,
  },
  more: {
    minHeight: touchTarget,
    minWidth: touchTarget,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: radius.sm,
  },
  secondaryLabel: {
    color: colors.textMuted,
    fontWeight: "600",
    fontSize: typeScale.sm,
  },
  menu: {
    marginTop: space.xs,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    paddingTop: space.xs,
    gap: 2,
  },
  menuItem: {
    minHeight: touchTarget,
    justifyContent: "center",
    paddingHorizontal: space.xs,
  },
  menuLabel: {
    color: colors.text,
    fontSize: typeScale.sm,
  },
});
