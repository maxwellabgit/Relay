import { Component, type ReactNode } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";

type Props = {
  readonly children: ReactNode;
};

type State = {
  readonly message: string | null;
};

/** Stops a render failure from blanking the product surface. */
export class ProductErrorBoundary extends Component<Props, State> {
  override state: State = { message: null };

  static getDerivedStateFromError(error: unknown): State {
    return { message: error instanceof Error ? error.message : "ui_failed" };
  }

  override render() {
    if (!this.state.message) return this.props.children;
    return (
      <View style={styles.wrap}>
        <Text style={styles.title} accessibilityRole="header">
          RELAY stopped this screen
        </Text>
        <Text style={styles.detail}>{this.state.message}</Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Try again"
          onPress={() => this.setState({ message: null })}
          style={styles.button}
        >
          <Text style={styles.buttonLabel}>Try again</Text>
        </Pressable>
      </View>
    );
  }
}

const styles = StyleSheet.create({
  wrap: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    padding: 24,
    backgroundColor: "#0b1018",
    gap: 12,
  },
  title: { color: "#f4f7fb", fontSize: 18, fontWeight: "700" },
  detail: { color: "#e06a6a", fontSize: 14, textAlign: "center" },
  button: {
    minHeight: 44,
    justifyContent: "center",
    paddingHorizontal: 16,
    borderRadius: 8,
    backgroundColor: "#2f6fed",
  },
  buttonLabel: { color: "#f4f7fb", fontWeight: "700" },
});
