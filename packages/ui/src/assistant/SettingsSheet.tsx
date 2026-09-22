import { SESSION_JEV_GRANT_DEFAULTS, type RelaySnapshot } from "@relay/contracts";
import { useState } from "react";
import { Modal, Platform, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from "react-native";
import { colors } from "../theme/colors.js";

export type SettingsSheetProps = {
  readonly visible: boolean;
  readonly snapshot: RelaySnapshot;
  readonly typeSafeKeyStatus: "present" | "disabled" | "unknown";
  readonly onClose: () => void;
  readonly onSetTypeSafeKey: (value: string) => Promise<void>;
  readonly onDeleteTypeSafeKey: () => Promise<void>;
  readonly onSetHostedProcessing: (enabled: boolean) => void;
  readonly onGrantJevDisclosure?: () => void;
  readonly onRevokeJevDisclosure?: (grantId: string) => void;
  readonly onRefreshHealth: () => void;
  readonly onExportDiagnostics?: () => string;
};

function chipDetail(snapshot: RelaySnapshot, id: string): { ok: boolean; detail: string } {
  const chip = snapshot.status.find((item) => item.id === id);
  return { ok: chip?.ok === true, detail: chip?.detail ?? "—" };
}

export function SettingsSheet({
  visible,
  snapshot,
  typeSafeKeyStatus,
  onClose,
  onSetTypeSafeKey,
  onDeleteTypeSafeKey,
  onSetHostedProcessing,
  onGrantJevDisclosure,
  onRevokeJevDisclosure,
  onRefreshHealth,
  onExportDiagnostics,
}: SettingsSheetProps) {
  const [keyDraft, setKeyDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const model = chipDetail(snapshot, "model");
  const audio = chipDetail(snapshot, "audio");
  const [exportText, setExportText] = useState<string | null>(null);
  const nativeMobile = Platform.OS === "ios" || Platform.OS === "android";

  const saveKey = async () => {
    const value = keyDraft.trim();
    if (!value) {
      setMessage("Enter a key to set or replace.");
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      await onSetTypeSafeKey(value);
      setKeyDraft("");
      setMessage("Key saved.");
      onRefreshHealth();
    } catch {
      setMessage("Could not save key.");
    } finally {
      setBusy(false);
    }
  };

  const deleteKey = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await onDeleteTypeSafeKey();
      setKeyDraft("");
      setMessage("Key deleted.");
      onRefreshHealth();
    } catch {
      setMessage("Could not delete key.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <View style={styles.sheet}>
          <View style={styles.header}>
            <Text style={styles.title}>Settings</Text>
            <Pressable accessibilityRole="button" onPress={onClose} style={styles.closeBtn}>
              <Text style={styles.closeLabel}>Close</Text>
            </Pressable>
          </View>
          <ScrollView contentContainerStyle={styles.scroll}>

          <View style={styles.section}>
            <Text style={styles.sectionTitle}>TypeSafe key</Text>
            <Text style={styles.meta}>
              Status: {typeSafeKeyStatus === "present" ? "present" : typeSafeKeyStatus === "disabled" ? "not set" : "—"}
            </Text>
            <TextInput
              value={keyDraft}
              onChangeText={setKeyDraft}
              placeholder="Paste key to set or replace"
              placeholderTextColor={colors.textDim}
              secureTextEntry
              autoCapitalize="none"
              autoCorrect={false}
              style={styles.input}
            />
            <View style={styles.row}>
              <Pressable
                accessibilityRole="button"
                disabled={busy}
                onPress={() => void saveKey()}
                style={[styles.btn, styles.btnPrimary]}
              >
                <Text style={styles.btnLabel}>{typeSafeKeyStatus === "present" ? "Replace" : "Set"}</Text>
              </Pressable>
              <Pressable
                accessibilityRole="button"
                disabled={busy || typeSafeKeyStatus !== "present"}
                onPress={() => void deleteKey()}
                style={[styles.btn, styles.btnDanger]}
              >
                <Text style={styles.btnLabel}>Delete</Text>
              </Pressable>
            </View>
          </View>

          <View style={styles.section}>
            <Text style={styles.sectionTitle}>Allow hosted processing</Text>
            <Text style={styles.meta}>
              Separate from Listening. Key present does not grant disclosure.
            </Text>
            <Pressable
              accessibilityRole="switch"
              accessibilityState={{ checked: snapshot.hostedProcessingEnabled }}
              onPress={() => onSetHostedProcessing(!snapshot.hostedProcessingEnabled)}
              style={[
                styles.toggle,
                snapshot.hostedProcessingEnabled ? styles.toggleOn : styles.toggleOff,
              ]}
            >
              <Text style={styles.toggleLabel}>
                {snapshot.hostedProcessingEnabled ? "On" : "Off"}
              </Text>
            </Pressable>
          </View>

          <View style={styles.section}>
            <Text style={styles.sectionTitle}>Session disclosure grant</Text>
            <Text style={styles.meta}>
              Required before an excerpt is sent. {SESSION_JEV_GRANT_DEFAULTS.maxRequests} requests,{" "}
              {SESSION_JEV_GRANT_DEFAULTS.maxBytes} bytes, 12 hours.
            </Text>
            {snapshot.jevDisclosure ? (
              <Text style={styles.meta}>
                Active {snapshot.jevDisclosure.grantId} · {snapshot.jevDisclosure.requestsUsed}/
                {snapshot.jevDisclosure.maxRequests} requests · {snapshot.jevDisclosure.bytesUsed}/
                {snapshot.jevDisclosure.maxBytes} bytes · until {snapshot.jevDisclosure.expiresAt}
              </Text>
            ) : (
              <Text style={styles.meta}>No active session grant.</Text>
            )}
            <View style={styles.row}>
              <Pressable
                accessibilityRole="button"
                disabled={!onGrantJevDisclosure}
                onPress={() => onGrantJevDisclosure?.()}
                style={[styles.btn, styles.btnPrimary]}
              >
                <Text style={styles.btnLabel}>Grant this session</Text>
              </Pressable>
              <Pressable
                accessibilityRole="button"
                disabled={!snapshot.jevDisclosure || !onRevokeJevDisclosure}
                onPress={() => {
                  if (snapshot.jevDisclosure) onRevokeJevDisclosure?.(snapshot.jevDisclosure.grantId);
                }}
                style={[styles.btn, styles.btnDanger]}
              >
                <Text style={styles.btnLabel}>Revoke</Text>
              </Pressable>
            </View>
          </View>

          <View style={styles.section}>
            <Text style={styles.sectionTitle}>Local model</Text>
            <Text style={styles.meta}>
              {model.ok ? "Ready" : "Unavailable"} · {model.detail}
            </Text>
            <Text style={styles.hint}>
              {nativeMobile
                ? "An on-device model is not installed. Download stays off until a tested model is selected."
                : "External llama.cpp-compatible server on 127.0.0.1:8080 (or RELAY_LOCAL_MODEL_PORT). Start with: ./dev/start-model.ps1 -StartHint"}
            </Text>
            <Pressable
              accessibilityRole="button"
              onPress={onRefreshHealth}
              style={[styles.btn, styles.btnSecondary]}
            >
              <Text style={styles.btnLabel}>Retry health check</Text>
            </Pressable>
          </View>

          <View style={styles.section}>
            <Text style={styles.sectionTitle}>Audio</Text>
            <Text style={styles.meta}>
              {audio.ok ? "Ready" : "Unavailable"} · {audio.detail}
            </Text>
            <Pressable
              accessibilityRole="button"
              onPress={onRefreshHealth}
              style={[styles.btn, styles.btnSecondary]}
            >
              <Text style={styles.btnLabel}>Retry health check</Text>
            </Pressable>
          </View>

          {onExportDiagnostics ? (
            <View style={styles.section}>
              <Text style={styles.sectionTitle}>Diagnostics</Text>
              <Text style={styles.hint}>
                Share a redacted run summary. It leaves out source transcripts and saved memory text.
              </Text>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel="Share diagnostics"
                onPress={() => setExportText(onExportDiagnostics())}
                style={[styles.btn, styles.btnSecondary]}
              >
                <Text style={styles.btnLabel}>Share diagnostics</Text>
              </Pressable>
              {exportText ? (
                <Text selectable style={styles.exportText}>
                  {exportText}
                </Text>
              ) : null}
            </View>
          ) : null}

          {message ? <Text style={styles.message}>{message}</Text> : null}
          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    backgroundColor: "rgba(0,0,0,0.55)",
    justifyContent: "center",
    alignItems: "center",
    padding: 20,
  },
  sheet: {
    width: "100%",
    maxWidth: 380,
    maxHeight: "90%",
    backgroundColor: colors.bgElevated,
    borderRadius: 14,
    borderWidth: 1,
    borderColor: colors.border,
    padding: 16,
    gap: 14,
  },
  scroll: {
    gap: 14,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  title: {
    color: colors.text,
    fontSize: 18,
    fontWeight: "700",
  },
  closeBtn: {
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  closeLabel: {
    color: colors.accent,
    fontSize: 14,
    fontWeight: "600",
  },
  section: {
    gap: 8,
    paddingTop: 4,
    borderTopWidth: 1,
    borderTopColor: colors.border,
  },
  sectionTitle: {
    color: colors.text,
    fontSize: 14,
    fontWeight: "700",
    marginTop: 4,
  },
  meta: {
    color: colors.textMuted,
    fontSize: 12,
  },
  exportText: {
    color: colors.text,
    fontSize: 12,
    lineHeight: 16,
  },
  hint: {
    color: colors.textDim,
    fontSize: 11,
    lineHeight: 15,
  },
  input: {
    backgroundColor: colors.inputBg,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 8,
    color: colors.text,
    paddingHorizontal: 10,
    paddingVertical: 8,
    fontSize: 13,
  },
  row: {
    flexDirection: "row",
    gap: 8,
  },
  btn: {
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 8,
    alignItems: "center",
  },
  btnPrimary: {
    backgroundColor: colors.accentSoft,
    borderWidth: 1,
    borderColor: colors.accent,
  },
  btnSecondary: {
    backgroundColor: colors.bgPanel,
    borderWidth: 1,
    borderColor: colors.borderStrong,
    alignSelf: "flex-start",
  },
  btnDanger: {
    backgroundColor: colors.chipBad,
    borderWidth: 1,
    borderColor: colors.danger,
  },
  btnLabel: {
    color: colors.text,
    fontSize: 13,
    fontWeight: "600",
  },
  toggle: {
    alignSelf: "flex-start",
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderWidth: 1,
  },
  toggleOn: {
    backgroundColor: colors.listenOn,
    borderColor: colors.ok,
  },
  toggleOff: {
    backgroundColor: colors.listenOff,
    borderColor: colors.borderStrong,
  },
  toggleLabel: {
    color: colors.text,
    fontWeight: "700",
    fontSize: 13,
  },
  message: {
    color: colors.warn,
    fontSize: 12,
  },
});
