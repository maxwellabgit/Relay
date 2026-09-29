import type { ComputerActivityView, ProjectCaseView, RelayCommand, WorkEpisode } from "@relay/contracts";
import { useState } from "react";
import { Pressable, ScrollView, Text, TextInput, View } from "react-native";
import { colors } from "../theme/colors.js";

type Props = {
  readonly activity: ComputerActivityView | undefined;
  readonly cases: readonly ProjectCaseView[];
  readonly onCommand?: (command: RelayCommand) => void;
};

const LABELS: Record<WorkEpisode["classification"]["label"], string> = {
  job_application: "Job Application",
  job_research: "Job Research",
  general_browsing: "Browsing",
  development: "Development", // pragma: allowlist secret
  unknown: "Activity",
};

function clock(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso.slice(11, 16);
  return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

function range(episode: WorkEpisode): string {
  const endMs = Date.parse(episode.endedAt ?? episode.startedAt);
  const live = episode.status === "open" && Number.isFinite(endMs) && Date.now() - endMs < 15 * 60 * 1000;
  const end = live ? "now" : clock(episode.endedAt ?? episode.startedAt);
  return `${clock(episode.startedAt)}–${end}`;
}

export function ActivityPane({ activity, cases, onCommand }: Props) {
  const [selected, setSelected] = useState<string | null>(null);
  const [domain, setDomain] = useState("");
  if (!activity) {
    return (
      <ScrollView contentContainerStyle={{ padding: 16 }}>
        <Text style={{ color: colors.textMuted }}>Observation is not available in this session.</Text>
      </ScrollView>
    );
  }
  const episode = [...activity.episodes, ...(activity.example ? [activity.example] : [])].find((item) => item.id === selected) ?? null;
  const settings = activity.settings;
  const send = (command: RelayCommand) => onCommand?.(command);
  return (
    <ScrollView style={{ flex: 1 }} contentContainerStyle={{ padding: 16, gap: 14 }}>
      <Text style={{ color: colors.text, fontSize: 18, fontWeight: "700" }} testID="relay-activity-heading">
        Today
      </Text>
      <Text testID="relay-observation-state" style={{ color: settings.enabled ? colors.ok : colors.textMuted }}>
        {settings.enabled ? "Observation on" : "Observation off"}
      </Text>
      {activity.current ? (
        <View testID="relay-activity-current" style={card}>
          <Text style={label}>Active now</Text>
          <Text style={title}>{activity.current.application}</Text>
          <Text style={body}>{activity.current.title}</Text>
          <Text style={body}>{`Likely activity ${activity.current.classification}`}</Text>
          <Text style={body}>{activity.current.caseLabel ? `Case ${activity.current.caseLabel}` : "No Case yet"}</Text>
          <Text style={meta}>Observing {activity.current.observing.join(" · ") || "nothing"}</Text>
          <Text style={meta}>Not observing {activity.current.notObserving.join(" · ")}</Text>
        </View>
      ) : (
        <Text style={meta}>Nothing active. RELAY will show work here after observation is on.</Text>
      )}
      {activity.episodes.map((item) => (
        <Pressable key={item.id} testID={`relay-episode-${item.id}`} onPress={() => setSelected(item.id)} style={card}>
          <Text style={meta}>{range(item)}</Text>
          <Text style={title}>{item.title}</Text>
          <Text style={body}>{LABELS[item.classification.label]}</Text>
          <Text style={meta}>{item.applications.join(" · ")}</Text>
          <Text style={meta}>{item.caseCandidateLabel ?? "Uncategorized"}</Text>
        </Pressable>
      ))}
      {activity.example ? (
        <Pressable testID="relay-episode-example" onPress={() => setSelected(activity.example?.id ?? null)} style={card}>
          <Text style={label}>Recorded example</Text>
          <Text style={meta}>{range(activity.example)}</Text>
          <Text style={title}>{activity.example.title}</Text>
          <Text style={body}>{LABELS[activity.example.classification.label]}</Text>
          <Text style={meta}>{activity.example.applications.join(" · ")}</Text>
          <Text style={meta}>{activity.example.caseCandidateLabel ?? "Uncategorized"}</Text>
        </Pressable>
      ) : null}
      {episode ? <EpisodeDetail episode={episode} cases={cases} onCommand={send} /> : null}
      <View style={card}>
        <Text style={label}>Observation settings</Text>
        <Toggle
          label="Observation"
          on={settings.enabled}
          onPress={() => send({ type: "SetActivityObservation", patch: { enabled: !settings.enabled } })}
        />
        <Toggle
          label="Windows"
          on={settings.windowsEnabled}
          onPress={() => send({ type: "SetActivityObservation", patch: { windowsEnabled: !settings.windowsEnabled } })}
        />
        <Toggle
          label="Chrome"
          on={settings.chromeEnabled}
          onPress={() => send({ type: "SetActivityObservation", patch: { chromeEnabled: !settings.chromeEnabled } })}
        />
        <Toggle
          label="Page content"
          on={settings.pageContentEnabled}
          onPress={() =>
            send({ type: "SetActivityObservation", patch: { pageContentEnabled: !settings.pageContentEnabled } })
          }
        />
        <Text style={meta}>{activity.retentionLabel}</Text>
        <Text style={meta}>{`Windows ${activity.windowsObserver} · Chrome ${activity.chromeConnection}`}</Text>
        <TextInput
          value={domain}
          onChangeText={setDomain}
          placeholder="permitted domain"
          placeholderTextColor={colors.textDim}
          autoCapitalize="none"
          style={input}
          testID="relay-domain-input"
        />
        <Pressable
          testID="relay-domain-permit"
          onPress={() => {
            const value = domain.trim();
            if (!value) return;
            send({ type: "PermitActivityDomain", domain: value });
            setDomain("");
          }}
          style={button}
        >
          <Text style={buttonText}>Permit domain</Text>
        </Pressable>
        {activity.permittedDomains.map((item) => (
          <Pressable key={item} onPress={() => send({ type: "RevokeActivityDomain", domain: item })}>
            <Text style={body}>{`${item} · revoke`}</Text>
          </Pressable>
        ))}
        <Pressable testID="relay-activity-clear" onPress={() => send({ type: "ClearActivityHistory" })} style={button}>
          <Text style={buttonText}>Clear observation history</Text>
        </Pressable>
        <Pressable
          testID="relay-replay-job"
          onPress={() => send({ type: "ReplayActivityFixture", fixture: "job-application" })}
          style={button}
        >
          <Text style={buttonText}>Replay job application example</Text>
        </Pressable>
      </View>
      <View style={card}>
        <Text style={label}>Trace</Text>
        {activity.trace.slice(0, 12).map((item) => (
          <Text key={item.id} style={meta}>{`${item.type} · ${item.message}`}</Text>
        ))}
      </View>
    </ScrollView>
  );
}

function EpisodeDetail({
  episode,
  cases,
  onCommand,
}: {
  readonly episode: WorkEpisode;
  readonly cases: readonly ProjectCaseView[];
  readonly onCommand: (command: RelayCommand) => void;
}) {
  return (
    <View testID="relay-episode-detail" style={card}>
      <Text style={label}>Episode</Text>
      <Text style={body}>{`${range(episode)} · ${episode.status}`}</Text>
      <Text style={body}>{episode.applications.join(" · ")}</Text>
      {episode.resources.map((item) => (
        <Text key={`${item.kind}:${item.label}`} style={body}>{`${item.kind} · ${item.label}`}</Text>
      ))}
      {episode.evidence.map((item) => (
        <Text key={`${item.observationId}:${item.kind}`} style={meta}>{`${item.kind} · ${item.summary}`}</Text>
      ))}
      <Text style={body}>{`Classification ${LABELS[episode.classification.label]} · ${episode.classification.confidence}`}</Text>
      {episode.classification.why.map((item) => (
        <Text key={item} style={meta}>{`Why ${item}`}</Text>
      ))}
      <Text style={body}>{episode.caseCandidateLabel ? `Case ${episode.caseCandidateLabel}` : "No Case"}</Text>
      {episode.caseEvidence.map((item) => (
        <Text key={item} style={meta}>{item}</Text>
      ))}
      <Text style={meta}>{`Observations ${episode.observationIds.join(", ") || "raw observations expired"}`}</Text>
      {cases.map((item) => (
        <Pressable
          key={item.projectCaseId}
          onPress={() =>
            onCommand({ type: "AssignActivityEpisode", episodeId: episode.id, caseId: item.projectCaseId })
          }
        >
          <Text style={body}>{`Assign ${item.alias}`}</Text>
        </Pressable>
      ))}
      {episode.caseCandidateLabel && !cases.some((item) => item.alias.toLowerCase() === episode.caseCandidateLabel?.toLowerCase()) ? (
        <Pressable
          testID="relay-create-case"
          onPress={() => {
            const alias = episode.caseCandidateLabel;
            if (!alias) return;
            onCommand({
              type: "AssignActivityEpisode",
              episodeId: episode.id,
              caseId: null,
              createAlias: alias,
            });
          }}
        >
          <Text style={body}>{`Create and assign ${episode.caseCandidateLabel}`}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

function Toggle({ label, on, onPress }: { readonly label: string; readonly on: boolean; readonly onPress: () => void }) {
  return (
    <Pressable accessibilityRole="switch" accessibilityState={{ checked: on }} onPress={onPress} style={{ paddingVertical: 6 }}>
      <Text style={body}>{`${label} ${on ? "on" : "off"}`}</Text>
    </Pressable>
  );
}

const card = {
  borderWidth: 1,
  borderColor: colors.border,
  borderRadius: 12,
  padding: 12,
  backgroundColor: colors.bgElevated,
  gap: 4,
} as const;

const label = { color: colors.textMuted, fontSize: 12, fontWeight: "700" as const };
const title = { color: colors.text, fontSize: 16, fontWeight: "700" as const };
const body = { color: colors.text, marginTop: 2 };
const meta = { color: colors.textMuted, marginTop: 2 };
const input = {
  color: colors.text,
  borderWidth: 1,
  borderColor: colors.border,
  borderRadius: 8,
  padding: 8,
  marginTop: 8,
};
const button = { marginTop: 8, paddingVertical: 8 };
const buttonText = { color: colors.accent, fontWeight: "700" as const };
