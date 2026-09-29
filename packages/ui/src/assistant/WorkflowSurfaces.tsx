import type { RelayCommand, WorkflowEpisodeView, WorkflowView } from "@relay/contracts";
import { useState } from "react";
import { Pressable, ScrollView, Text, TextInput, View } from "react-native";
import { colors } from "../theme/colors.js";

type Command = (command: RelayCommand) => void;

export function WorkflowSurfaces({
  mode,
  workflow,
  cases,
  onCommand,
}: {
  readonly mode: "today" | "now" | "reflex" | "observe" | "verify";
  readonly workflow: WorkflowView | undefined;
  readonly cases: readonly { readonly projectCaseId: string; readonly alias: string }[];
  readonly onCommand?: Command;
}) {
  const [selected, setSelected] = useState<string | null>(null);
  if (!workflow) {
    return <Text style={{ color: colors.textMuted, padding: 16 }}>Activity is not available in this session.</Text>;
  }
  if (mode === "now") return <NowView workflow={workflow} />;
  if (mode === "observe") return <ObserveView workflow={workflow} {...(onCommand ? { onCommand } : {})} />;
  if (mode === "reflex") return <ReflexView workflow={workflow} {...(onCommand ? { onCommand } : {})} />;
  if (mode === "verify") return <WorkflowVerify workflow={workflow} {...(onCommand ? { onCommand } : {})} />;
  const episode = workflow.episodes.find((item) => item.episodeId === selected);
  if (episode) {
    return (
      <EpisodeDetail
        episode={episode}
        cases={cases}
        onBack={() => setSelected(null)}
        {...(onCommand ? { onCommand } : {})}
        workflow={workflow}
      />
    );
  }
  return <TodayView workflow={workflow} onOpen={setSelected} />;
}

function TodayView({ workflow, onOpen }: { readonly workflow: WorkflowView; readonly onOpen: (id: string) => void }) {
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 12 }}>
      <Text style={{ color: colors.text, fontWeight: "700" }}>Today</Text>
      {!workflow.settings.setupComplete ? (
        <Text style={{ color: colors.warn }}>Observation is not set up yet. Open Observe to choose what RELAY may see.</Text>
      ) : null}
      {workflow.settings.paused ? <Text style={{ color: colors.warn }}>Observation is paused.</Text> : null}
      {workflow.host.windows === "failed" ? <Text style={{ color: colors.danger }}>{workflow.host.windowsDetail}</Text> : null}
      {workflow.host.chrome === "disconnected" && workflow.settings.chromeEnabled ? (
        <Text style={{ color: colors.warn }}>{workflow.host.chromeDetail || "Chrome is not connected."}</Text>
      ) : null}
      {workflow.episodes.length === 0 ? (
        <Text style={{ color: colors.textMuted }}>
          {workflow.settings.paused
            ? "Nothing new will appear while observation is paused."
            : "No work episodes yet. Leave RELAY running and work normally. Episodes will show up here."}
        </Text>
      ) : null}
      {workflow.episodes.map((episode) => (
        <Pressable key={episode.episodeId} accessibilityRole="button" onPress={() => onOpen(episode.episodeId)} testID={`episode-${episode.episodeId}`}>
          <View style={{ borderWidth: 1, borderColor: colors.border, borderRadius: 12, padding: 12, gap: 4 }}>
            <Text style={{ color: colors.text, fontWeight: "700" }}>{labelFor(episode)}</Text>
            <Text style={{ color: colors.textMuted }}>{`${clock(episode.startedAt)}–${clock(episode.endedAt)} · ${episode.outcome}`}</Text>
            <Text style={{ color: colors.textMuted }}>{episode.evidence.map((item) => item.application || item.host || item.label).filter(Boolean).slice(0, 4).join(" · ")}</Text>
            <Text style={{ color: episode.uncertain ? colors.warn : colors.textMuted }}>
              {episode.caseId ? `Case ${episode.caseId}` : "No Case yet"}
              {episode.uncertain ? " · Uncertain" : ""}
            </Text>
          </View>
        </Pressable>
      ))}
    </ScrollView>
  );
}

function NowView({ workflow }: { readonly workflow: WorkflowView }) {
  const current = workflow.current;
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 10 }}>
      <Text style={{ color: colors.text, fontWeight: "700" }}>Current activity</Text>
      <Text style={{ color: colors.text }}>{`Windows: ${workflow.host.windows}. ${workflow.host.windowsDetail}`}</Text>
      <Text style={{ color: colors.text }}>{`Chrome: ${workflow.host.chrome}. ${workflow.host.chromeDetail}`}</Text>
      {current ? (
        <View style={{ borderWidth: 1, borderColor: colors.border, borderRadius: 12, padding: 12, gap: 6 }}>
          <Text style={{ color: colors.text }}>{current.application || "Unknown application"}</Text>
          <Text style={{ color: colors.textMuted }}>{current.title || "No window title"}</Text>
          <Text style={{ color: colors.textMuted }}>{`Since ${clock(current.since ?? "")}`}</Text>
          <Text style={{ color: current.uncertain ? colors.warn : colors.text }}>
            {current.classification ? `RELAY thinks this is ${current.classification.split("_").join(" ")}.` : "RELAY has not classified this yet."}
          </Text>
        </View>
      ) : (
        <Text style={{ color: colors.textMuted }}>
          {workflow.settings.paused ? "Observation is paused, so RELAY is not watching." : "RELAY is not seeing an active window yet."}
        </Text>
      )}
    </ScrollView>
  );
}

function EpisodeDetail({
  episode,
  cases,
  workflow,
  onBack,
  onCommand,
}: {
  readonly episode: WorkflowEpisodeView;
  readonly cases: readonly { readonly projectCaseId: string; readonly alias: string }[];
  readonly workflow: WorkflowView;
  readonly onBack: () => void;
  readonly onCommand?: Command;
}) {
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 10 }}>
      <Pressable accessibilityRole="button" onPress={onBack}>
        <Text style={{ color: colors.accent }}>Back to Today</Text>
      </Pressable>
      <Text style={{ color: colors.text, fontWeight: "700" }}>{labelFor(episode)}</Text>
      <Text style={{ color: colors.text }}>{episode.rationale}</Text>
      <Text style={{ color: colors.textMuted }}>{`${episode.startedAt} → ${episode.endedAt}`}</Text>
      <Text style={{ color: colors.text, fontWeight: "700" }}>Evidence</Text>
      {episode.evidence.map((item) => (
        <Text key={item.observationId} style={{ color: colors.textMuted }}>
          {`${clock(item.at)} · ${item.application || item.host || "source"} · ${item.label}`}
        </Text>
      ))}
      <Text style={{ color: colors.text, fontWeight: "700" }}>Case</Text>
      {cases.map((item) => (
        <Pressable
          key={item.projectCaseId}
          accessibilityRole="button"
          onPress={() => onCommand?.({ type: "Workflow", action: "assign_case", episodeId: episode.episodeId, caseId: item.projectCaseId })}
        >
          <Text style={{ color: episode.caseId === item.projectCaseId ? colors.accent : colors.text }}>{item.alias}</Text>
        </Pressable>
      ))}
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "ensure_job_case" })}>
        <Text style={{ color: colors.accent }}>Create Job Search case</Text>
      </Pressable>
      <Pressable
        accessibilityRole="button"
        onPress={() => onCommand?.({ type: "Workflow", action: "correct", episodeId: episode.episodeId, classification: "job_research" })}
      >
        <Text style={{ color: colors.text }}>Mark as research</Text>
      </Pressable>
      <Pressable
        accessibilityRole="button"
        onPress={() => onCommand?.({ type: "Workflow", action: "correct", episodeId: episode.episodeId, classification: "uncertain" })}
      >
        <Text style={{ color: colors.text }}>Mark as uncertain</Text>
      </Pressable>
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "record_outcome", episodeId: episode.episodeId, outcome: "submitted" })}>
        <Text style={{ color: colors.text }}>I submitted this application</Text>
      </Pressable>
      {episode.classification === "uncertain" ? (
        <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "judge_episode", episodeId: episode.episodeId })}>
          <Text style={{ color: colors.accent }}>Ask Jev to classify</Text>
        </Pressable>
      ) : null}
      {workflow.judgment?.episodeId === episode.episodeId ? (
        <Text style={{ color: colors.textMuted }}>
          {`Jev options ${workflow.judgment.options.join(", ")}. Selected ${workflow.judgment.selected ?? "none"}. ${workflow.judgment.reasonCode}.`}
        </Text>
      ) : null}
      {workflow.reflex?.state === "active" && episode.classification === "job_application" ? (
        <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "run_draft", episodeId: episode.episodeId })}>
          <Text style={{ color: colors.accent }}>Create resume and cover letter copies</Text>
        </Pressable>
      ) : null}
      {workflow.reflex?.state === "shadow" && episode.classification === "job_application" ? (
        <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "preview_draft", episodeId: episode.episodeId })}>
          <Text style={{ color: colors.accent }}>Preview what the Reflex would write</Text>
        </Pressable>
      ) : null}
      {workflow.shadowPreview ? (
        <View style={{ gap: 6 }}>
          <Text style={{ color: colors.text, fontWeight: "700" }}>Shadow preview. Nothing was written.</Text>
          <Text style={{ color: colors.textMuted }}>{workflow.shadowPreview.resume}</Text>
        </View>
      ) : null}
      {workflow.lastDraft ? (
        <Text style={{ color: colors.ok }}>{`Wrote ${workflow.lastDraft.resumePath} and ${workflow.lastDraft.coverLetterPath}. Receipt ${workflow.lastDraft.receiptId}.`}</Text>
      ) : null}
    </ScrollView>
  );
}

function WorkflowVerify({ workflow, onCommand }: { readonly workflow: WorkflowView; readonly onCommand?: Command }) {
  const pending = workflow.proposals.filter((item) => item.state === "pending");
  if (pending.length === 0) return <Text style={{ color: colors.textMuted, padding: 16 }}>No workflow proposals.</Text>;
  return (
    <View style={{ padding: 16, gap: 12 }}>
      {pending.map((item) => (
        <View key={item.proposalId} style={{ borderWidth: 1, borderColor: colors.border, borderRadius: 12, padding: 12, gap: 6 }}>
          <Text style={{ color: colors.text, fontWeight: "700" }}>{item.title}</Text>
          <Text style={{ color: colors.text }}>{item.explanation}</Text>
          <Text style={{ color: colors.textMuted }}>{`Evidence ${item.episodeIds.join(", ")}`}</Text>
          {item.kind === "pattern" ? (
            <View style={{ flexDirection: "row", gap: 12 }}>
              <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_proposal", proposalId: item.proposalId, decision: "accept" })}>
                <Text style={{ color: colors.accent }}>Create Reflex</Text>
              </Pressable>
              <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_proposal", proposalId: item.proposalId, decision: "reject" })}>
                <Text style={{ color: colors.text }}>Reject</Text>
              </Pressable>
              <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_proposal", proposalId: item.proposalId, decision: "suppress" })}>
                <Text style={{ color: colors.text }}>Never infer this</Text>
              </Pressable>
            </View>
          ) : (
            <ImprovementActions proposalId={item.proposalId} {...(onCommand ? { onCommand } : {})} />
          )}
        </View>
      ))}
    </View>
  );
}

function ImprovementActions({ proposalId, onCommand }: { readonly proposalId: string; readonly onCommand?: Command }) {
  const [text, setText] = useState("");
  return (
    <View style={{ gap: 8 }}>
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_improvement", proposalId, decision: "accept" })}>
        <Text style={{ color: colors.accent }}>Accept preference</Text>
      </Pressable>
      <TextInput
        accessibilityLabel="Edit preference"
        value={text}
        onChangeText={setText}
        placeholder="Edit the preference"
        placeholderTextColor={colors.textDim}
        style={{ color: colors.text, borderWidth: 1, borderColor: colors.border, minHeight: 44, paddingHorizontal: 8 }}
      />
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_improvement", proposalId, decision: "edit", preference: text })}>
        <Text style={{ color: colors.text }}>Save edited preference</Text>
      </Pressable>
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_improvement", proposalId, decision: "reject" })}>
        <Text style={{ color: colors.text }}>Reject</Text>
      </Pressable>
      <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "decide_improvement", proposalId, decision: "suppress" })}>
        <Text style={{ color: colors.text }}>Never infer this</Text>
      </Pressable>
    </View>
  );
}

function ReflexView({ workflow, onCommand }: { readonly workflow: WorkflowView; readonly onCommand?: Command }) {
  const reflex = workflow.reflex;
  if (!reflex) return <Text style={{ color: colors.textMuted, padding: 16 }}>No Job Application Reflex yet. Repeated episodes can propose one in Verify.</Text>;
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 8 }}>
      <Text style={{ color: colors.text, fontWeight: "700" }}>{`${reflex.reflexId} v${reflex.version} · ${reflex.state}`}</Text>
      <Text style={{ color: colors.text }}>{`Trigger: ${reflex.trigger}`}</Text>
      <Text style={{ color: colors.text }}>{`Can: ${reflex.capabilities.join(", ")}`}</Text>
      <Text style={{ color: colors.text }}>{`Cannot: ${reflex.forbidden.join(", ")}`}</Text>
      <Text style={{ color: colors.text }}>{`Approval: ${reflex.approval}`}</Text>
      {reflex.procedure.map((step) => (
        <Text key={step} style={{ color: colors.textMuted }}>{`• ${step}`}</Text>
      ))}
      {reflex.preferences.map((item) => (
        <Text key={item} style={{ color: colors.text }}>{`Preference: ${item}`}</Text>
      ))}
      {reflex.history.map((item) => (
        <Text key={`${item.version}-${item.at}`} style={{ color: colors.textMuted }}>{`v${item.version} ${item.at} ${item.note}`}</Text>
      ))}
      {reflex.runs.map((run) => (
        <Text key={run.receiptId} style={{ color: colors.textMuted }}>{`${run.at} receipt ${run.receiptId} ${run.resumePath}`}</Text>
      ))}
      {reflex.state === "shadow" ? (
        <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "activate_reflex" })}>
          <Text style={{ color: colors.accent }}>Approve and activate</Text>
        </Pressable>
      ) : null}
      {reflex.state === "active" ? (
        <Pressable accessibilityRole="button" onPress={() => onCommand?.({ type: "Workflow", action: "pause_reflex" })}>
          <Text style={{ color: colors.text }}>Pause Reflex</Text>
        </Pressable>
      ) : null}
    </ScrollView>
  );
}

function ObserveView({ workflow, onCommand }: { readonly workflow: WorkflowView; readonly onCommand?: Command }) {
  const settings = workflow.settings;
  const [site, setSite] = useState("");
  const [folder, setFolder] = useState("");
  const [master, setMaster] = useState(settings.masterResumePath ?? "");
  const send = (patch: Extract<RelayCommand, { type: "Workflow"; action: "configure" }>) => onCommand?.(patch);
  return (
    <ScrollView contentContainerStyle={{ padding: 16, gap: 10 }}>
      <Text style={{ color: colors.text, fontWeight: "700" }}>Observation</Text>
      <Text style={{ color: colors.textMuted }}>
        RELAY can watch the foreground window and sites you allow. It does not record the screen or keystrokes. Raw activity expires after {settings.retentionDays} days. Cases and approved Reflexes stay.
      </Text>
      {!settings.setupComplete ? <Text style={{ color: colors.warn }}>Choose what to allow, then finish setup. Nothing is collected until you do.</Text> : null}
      <Toggle label={settings.paused ? "Resume observation" : "Pause all observation"} onPress={() => send({ type: "Workflow", action: "configure", paused: !settings.paused })} />
      <Toggle label={`Windows foreground ${settings.windowsEnabled ? "on" : "off"}`} onPress={() => send({ type: "Workflow", action: "configure", windowsEnabled: !settings.windowsEnabled })} />
      <Toggle label={`Chrome ${settings.chromeEnabled ? "on" : "off"}`} onPress={() => send({ type: "Workflow", action: "configure", chromeEnabled: !settings.chromeEnabled })} />
      <Toggle
        label={`Page text ${settings.pageContentEnabled ? "on" : "off"}`}
        onPress={() => send({ type: "Workflow", action: "configure", pageContentEnabled: !settings.pageContentEnabled })}
      />
      <Text style={{ color: colors.text }}>{`Windows observer: ${workflow.host.windows}. ${workflow.host.windowsDetail}`}</Text>
      <Text style={{ color: colors.text }}>{`Chrome bridge: ${workflow.host.chrome}. ${workflow.host.chromeDetail}`}</Text>
      <Text style={{ color: colors.textMuted }}>Allowed sites: {settings.allowedSites.join(", ") || "none"}</Text>
      <Text style={{ color: colors.textMuted }}>This session: {workflow.sessionSites.join(", ") || "none"}</Text>
      <TextInput accessibilityLabel="Site host" value={site} onChangeText={setSite} placeholder="www.linkedin.com" placeholderTextColor={colors.textDim} style={field} />
      <Toggle label="Allow site" onPress={() => send({ type: "Workflow", action: "configure", allowedSites: [...settings.allowedSites, site.trim()] })} />
      <Toggle label="Allow site for this session" onPress={() => send({ type: "Workflow", action: "configure", allowSessionSite: site.trim() })} />
      <TextInput accessibilityLabel="Folder" value={folder} onChangeText={setFolder} placeholder="C:\\Users\\me\\Documents\\Job Search" placeholderTextColor={colors.textDim} style={field} />
      <Toggle label="Allow folder" onPress={() => send({ type: "Workflow", action: "configure", allowedFolders: [...settings.allowedFolders, folder.trim()] })} />
      <Text style={{ color: colors.textMuted }}>Allowed folders: {settings.allowedFolders.join(", ") || "none"}</Text>
      <TextInput accessibilityLabel="Master resume path" value={master} onChangeText={setMaster} placeholder="C:\\Job\\master.md" placeholderTextColor={colors.textDim} style={field} />
      <Toggle label="Save master resume" onPress={() => onCommand?.({ type: "Workflow", action: "set_master_resume", path: master.trim() })} />
      <Toggle label="Keep raw activity for 7 days" onPress={() => send({ type: "Workflow", action: "configure", retentionDays: 7 })} />
      <Toggle label="Delete observation history" onPress={() => onCommand?.({ type: "Workflow", action: "delete_observations" })} />
      <Toggle
        label="Finish setup"
        onPress={() => send({ type: "Workflow", action: "configure", setupComplete: true, paused: false })}
      />
    </ScrollView>
  );
}

function Toggle({ label, onPress }: { readonly label: string; readonly onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress}>
      <Text style={{ color: colors.accent }}>{label}</Text>
    </Pressable>
  );
}

const field = { color: colors.text, borderWidth: 1, borderColor: colors.border, minHeight: 44, paddingHorizontal: 8 };

function labelFor(episode: WorkflowEpisodeView): string {
  if (episode.classification === "job_application") return `${episode.position || "Job application"}${episode.company ? ` · ${episode.company}` : ""}`;
  if (episode.classification === "job_research") return `Research${episode.company ? ` · ${episode.company}` : ""}`;
  if (episode.classification === "uncertain") return "Uncertain activity";
  return "Activity";
}

function clock(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}
