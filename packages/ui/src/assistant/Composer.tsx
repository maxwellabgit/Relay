import { useState } from "react";
import { Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import { colors } from "../theme/colors.js";
import { radius, space, touchTarget, typeScale } from "../theme/tokens.js";

type Props = {
  readonly onSubmit: (text: string) => void;
  readonly onCancel?: () => void;
  readonly disabled?: boolean;
  readonly listening?: boolean;
  readonly busy?: boolean;
  readonly waitingLabel?: string | null;
};

export function Composer({
  onSubmit,
  onCancel,
  disabled = false,
  listening = false,
  busy = false,
  waitingLabel = null,
}: Props) {
  const [text, setText] = useState("");

  const send = () => {
    const trimmed = text.trim();
    if (!trimmed || disabled || busy) return;
    onSubmit(trimmed);
    setText("");
  };

  const placeholder = listening ? "Listening… you can still ask" : "Ask RELAY";

  return (
    <View style={styles.wrap} testID="relay-composer">
      {waitingLabel ? (
        <Text style={styles.waiting} accessibilityLiveRegion="polite">
          {waitingLabel}
        </Text>
      ) : null}
      <View style={styles.row}>
        <TextInput
          testID="relay-composer-input"
          accessibilityLabel="Ask RELAY"
          value={text}
          onChangeText={setText}
          placeholder={placeholder}
          placeholderTextColor={colors.textDim}
          editable={!disabled}
          style={styles.input}
          onSubmitEditing={send}
          onKeyPress={(event) => {
            if (event.nativeEvent.key === "Enter" && !(event.nativeEvent as { shiftKey?: boolean }).shiftKey) {
              event.preventDefault?.();
              send();
            }
          }}
          returnKeyType="send"
          multiline
          blurOnSubmit
        />
        {busy && onCancel ? (
          <Pressable
            testID="relay-composer-cancel"
            accessibilityRole="button"
            accessibilityLabel="Stop current request"
            onPress={onCancel}
            style={styles.cancel}
          >
            <Text style={styles.cancelText}>Stop</Text>
          </Pressable>
        ) : (
          <Pressable
            testID="relay-composer-send"
            accessibilityRole="button"
            accessibilityLabel="Send"
            onPress={send}
            disabled={disabled || busy || !text.trim()}
            style={[styles.button, (!text.trim() || disabled || busy) && styles.buttonDisabled]}
          >
            <Text style={styles.buttonText}>Send</Text>
          </Pressable>
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    backgroundColor: colors.bgElevated,
    gap: space.xs,
  },
  waiting: {
    color: colors.warn,
    fontSize: typeScale.xs,
  },
  row: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: space.sm,
  },
  input: {
    flex: 1,
    minHeight: touchTarget,
    maxHeight: 120,
    backgroundColor: colors.inputBg,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    color: colors.text,
    paddingHorizontal: space.sm,
    paddingVertical: space.sm,
    fontSize: typeScale.sm,
  },
  button: {
    minHeight: touchTarget,
    minWidth: touchTarget,
    justifyContent: "center",
    backgroundColor: colors.accent,
    borderRadius: radius.md,
    paddingHorizontal: space.md,
  },
  cancel: {
    minHeight: touchTarget,
    justifyContent: "center",
    backgroundColor: colors.danger,
    borderRadius: radius.md,
    paddingHorizontal: space.md,
  },
  buttonDisabled: {
    opacity: 0.4,
  },
  buttonText: {
    color: colors.text,
    fontWeight: "700",
    fontSize: typeScale.sm,
  },
  cancelText: {
    color: colors.text,
    fontWeight: "700",
    fontSize: typeScale.sm,
  },
});
