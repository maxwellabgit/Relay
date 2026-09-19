import { Pressable, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly listening: boolean;
  readonly onChange: (enabled: boolean) => void;
};

export function ListenToggle({ listening, onChange }: Props) {
  return (
    <View style={styles.wrap}>
      <Text style={styles.brand}>RELAY</Text>
      <Pressable
        accessibilityRole="switch"
        accessibilityState={{ checked: listening }}
        onPress={() => onChange(!listening)}
        style={[styles.toggle, listening ? styles.on : styles.off]}
      >
        <View style={[styles.knob, listening ? styles.knobOn : styles.knobOff]} />
        <Text style={styles.label}>{listening ? "Listening" : "Paused"}</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 16,
    paddingTop: 16,
    paddingBottom: 8,
  },
  brand: {
    color: colors.text,
    fontSize: 22,
    fontWeight: "700",
    letterSpacing: 1.5,
  },
  toggle: {
    flexDirection: "row",
    alignItems: "center",
    borderRadius: 20,
    paddingVertical: 8,
    paddingHorizontal: 12,
    gap: 10,
    borderWidth: 1,
    borderColor: colors.border,
  },
  on: {
    backgroundColor: colors.listenOn,
  },
  off: {
    backgroundColor: colors.listenOff,
  },
  knob: {
    width: 18,
    height: 18,
    borderRadius: 9,
  },
  knobOn: {
    backgroundColor: colors.accent,
  },
  knobOff: {
    backgroundColor: colors.textDim,
  },
  label: {
    color: colors.text,
    fontSize: 13,
    fontWeight: "600",
  },
});
