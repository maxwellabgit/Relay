import { StatusBar } from "expo-status-bar";
import { StyleSheet, Text, View } from "react-native";

export function App() {
  return (
    <View style={styles.root}>
      <Text style={styles.brand}>RELAY</Text>
      <Text style={styles.copy}>Windows workbench scaffold — shared Expo UI lands in commit 7.</Text>
      <StatusBar style="auto" />
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#0f1419",
    padding: 24,
  },
  brand: {
    color: "#f4f7fb",
    fontSize: 42,
    fontWeight: "700",
    letterSpacing: 2,
    marginBottom: 12,
  },
  copy: {
    color: "#9aa7b5",
    fontSize: 16,
    textAlign: "center",
    maxWidth: 420,
  },
});
