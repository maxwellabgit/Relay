import type { StatusChipState } from "@relay/contracts";
import { StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly status: readonly StatusChipState[];
};

export function StatusChips({ status }: Props) {
  return (
    <View style={styles.row}>
      {status.map((chip) => (
        <View
          key={chip.id}
          style={[styles.chip, chip.ok ? styles.chipOk : styles.chipBad]}
        >
          <Text style={styles.label}>{chip.label}</Text>
          <Text style={styles.detail}>{chip.detail}</Text>
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    paddingHorizontal: 16,
    paddingVertical: 8,
  },
  chip: {
    borderRadius: 8,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: 10,
    paddingVertical: 6,
    minWidth: 72,
  },
  chipOk: {
    backgroundColor: colors.chipOk,
  },
  chipBad: {
    backgroundColor: colors.chipBad,
  },
  label: {
    color: colors.text,
    fontSize: 12,
    fontWeight: "600",
  },
  detail: {
    color: colors.textMuted,
    fontSize: 10,
    marginTop: 2,
  },
});
