import type {
  CaseExecutionStepView,
  CaseExecutionView,
  DecisionAttemptView,
  DecisionReceiptView,
  PatternView,
  RelaySnapshot,
  TraceRow,
} from "@relay/contracts";
import { useMemo, useState, type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { colors } from "../theme/colors.js";
import { touchTarget } from "../theme/tokens.js";
import {
  copyableRunIds,
  exportTraceSelection,
  filterConsoleTrace,
  traceProvider,
  traceSeverity,
  type ConsoleSeverity,
} from "./console-trace.js";
import { JevDecisionTree } from "./JevDecisionTree.js";
import { projectJevTree } from "./projectJevTree.js";

type Props = {
  readonly snapshot: RelaySnapshot;
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
  readonly onOpenLog?: () => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
  readonly onInjectSample?: () => void;
};

type Tab = "case" | "decisions" | "events" | "evidence";

const SPEEDS = [0, 1, 10] as const;
const TABS: ReadonlyArray<{ id: Tab; label: string }> = [
  { id: "case", label: "Current Case" },
  { id: "decisions", label: "Decisions" },
  { id: "events", label: "Run Events" },
  { id: "evidence", label: "Evidence" },
];

export function DeveloperConsole({
  snapshot,
  onReplayFixture,
  onStartSession,
  onEndSession,
  onOpenLog,
  onApproveCandidate,
  onRejectCandidate,
  onSnoozeCandidate,
  onInjectSample,
}: Props) {
  const [tab, setTab] = useState<Tab>("case");
  const [speed, setSpeed] = useState<number>(0);
  const [paused, setPaused] = useState(false);
  const [frozen, setFrozen] = useState(snapshot.trace);
  const [stageFilter, setStageFilter] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [reasonFilter, setReasonFilter] = useState<string | null>(null);
  const [runFilter, setRunFilter] = useState<string | null>(null);
  const [caseFilter, setCaseFilter] = useState<string | null>(null);
  const [providerFilter, setProviderFilter] = useState<string | null>(null);
  const [severityFilter, setSeverityFilter] = useState<ConsoleSeverity | null>(null);

  const rows = paused ? frozen : snapshot.trace;
  const receivedRequest = receivedRequestText(snapshot);
  const tree = useMemo(
    () => projectJevTree(snapshot.decision, receivedRequest),
    [snapshot.decision, receivedRequest],
  );

  const filtered = filterConsoleTrace(rows, {
    runId: runFilter,
    caseId: caseFilter,
    provider: providerFilter,
    severity: severityFilter,
    stage: stageFilter,
    status: statusFilter,
    reason: reasonFilter,
  });

  const stageChips = uniqueValues(rows.map((r) => r.stage));
  const statusChips = uniqueValues(rows.map((r) => r.status));
  const reasonChips = uniqueValues(rows.map((r) => r.reasonCode));
  const chronological = [...filtered].sort((a, b) => a.sequence - b.sequence);

  return (
    <ScrollView style={styles.panel} contentContainerStyle={styles.content}>
      <View style={styles.tabs}>
        {TABS.map((item) => (
          <Pressable
            key={item.id}
            onPress={() => setTab(item.id)}
            style={[styles.tab, tab === item.id ? styles.tabActive : null]}
          >
            <Text style={[styles.tabText, tab === item.id ? styles.tabTextActive : null]}>{item.label}</Text>
          </Pressable>
        ))}
      </View>

      {tab === "case" ? (
        <View style={styles.caseLayout}>
          <OverviewPane
            snapshot={snapshot}
            {...(onOpenLog !== undefined ? { onOpenLog } : {})}
            {...(onStartSession !== undefined ? { onStartSession } : {})}
            {...(onEndSession !== undefined ? { onEndSession } : {})}
          />
          <View style={styles.jevLayout}>
            <View style={styles.jevMain}>
              <JevDecisionTree tree={tree} />
            </View>
            <View style={styles.jevSide}>
              <SideCard title="Current input">
                <Text style={styles.sideBody}>{receivedRequest ?? "No request text yet."}</Text>
                <Text style={styles.sideMeta}>
                  {snapshot.currentInputPreview ? "not saved" : "preview cleared"}
                  {tree.caseId ? ` · case ${tree.caseId}` : ""}
                  {tree.decisionId ? ` · decision ${tree.decisionId}` : ""}
                  {tree.historical ? " · historical" : ""}
                </Text>
              </SideCard>
              <SideCard title="Trace">
                {[...chronological].slice(-6).reverse().map((row) => (
                  <Text key={row.sequence} style={styles.traceMini}>
                    {`${formatTime(row.at)} · ${row.stage ?? row.type} · ${row.status ?? "-"}`}
                  </Text>
                ))}
                <Text style={tree.completed ? styles.footerOk : styles.footerWait}>
                  {tree.completed
                    ? `Completed${tree.totalMs != null ? ` in ${Math.round(tree.totalMs)} ms` : ""}`
                    : "In progress"}
                </Text>
              </SideCard>
              <SideCard title="Hosted grant">
                <Text style={styles.sideBody}>
                  {snapshot.jevDisclosure
                    ? `${snapshot.jevDisclosure.grantId} · ${snapshot.jevDisclosure.requestsUsed}/${snapshot.jevDisclosure.maxRequests} requests · ${snapshot.jevDisclosure.bytesUsed}/${snapshot.jevDisclosure.maxBytes} bytes`
                    : "No active grant."}
                </Text>
              </SideCard>
              <GateDetails gate={snapshot.gate} />
              <JudgmentEvidence attempts={snapshot.decision?.attempts ?? []} />
            </View>
          </View>
        </View>
      ) : null}

      {tab === "decisions" ? (
        <View style={styles.jevSide}>
          {hasJudgmentEvidence(snapshot) ? (
            <>
              <JudgmentEvidence attempts={snapshot.decision?.attempts ?? []} />
              <SideCard title="Jev output">
                <Text style={styles.code}>{formatJson(tree.output)}</Text>
              </SideCard>
              <SideCard title="Evidence">
                {snapshot.patterns.length === 0 ? (
                  <Text style={styles.empty}>No completed-episode patterns yet.</Text>
                ) : (
                  snapshot.patterns.slice(0, 4).map((pattern) => (
                    <Text key={pattern.signature} style={styles.sideBody}>
                      {`${pattern.signature} · ${pattern.count}× · ${pattern.candidateState ?? "observing"}`}
                    </Text>
                  ))
                )}
              </SideCard>
            </>
          ) : (
            <SideCard title="Decisions">
              <Text style={styles.empty}>
                No Jev judgment events for this Case. Deterministic paths show not_observed here.
              </Text>
            </SideCard>
          )}
        </View>
      ) : null}

      {tab === "evidence" ? (
        <View>
          <SideCard title="Invocations">
            {(snapshot.reflexInvocations ?? []).length === 0 ? (
              <Text style={styles.empty}>No Reflex invocation yet.</Text>
            ) : (
              (snapshot.reflexInvocations ?? []).map((item) => (
                <Text key={item.invocationId} style={styles.meta}>
                  {`${item.resultType} · ${item.reflexId} · ${item.authority} · tools ${item.receiptIds.join(",") || "none"} · ${item.invocationId}`}
                </Text>
              ))
            )}
          </SideCard>
          <SideCard title="Sample">
            {onInjectSample ? (
              <Pressable accessibilityRole="button" accessibilityLabel="Inject calendar sample" onPress={onInjectSample}>
                <Text style={styles.meta}>Inject calendar sample</Text>
              </Pressable>
            ) : (
              <Text style={styles.empty}>Sample injection is off.</Text>
            )}
          </SideCard>
          <SideCard title="Bindings">
            {(snapshot.observationBindings ?? []).map((item) => (
              <Text key={item.bindingId} style={styles.meta}>
                {`${item.resourceId} · ${item.enabled ? "observing" : "stopped"} · last ${item.lastSyncAt ?? "not checked"}`}
              </Text>
            ))}
          </SideCard>
        </View>
      ) : null}

      {tab === "events" ? (
        <LogsPane
          snapshot={snapshot}
          chronological={chronological}
          stageChips={stageChips}
          statusChips={statusChips}
          reasonChips={reasonChips}
          stageFilter={stageFilter}
          statusFilter={statusFilter}
          reasonFilter={reasonFilter}
          runChips={uniqueValues(rows.map((row) => row.runId))}
          caseChips={uniqueValues(rows.map((row) => row.caseId))}
          providerChips={uniqueValues(rows.map((row) => traceProvider(row)))}
          severityChips={uniqueValues(rows.map((row) => traceSeverity(row)))}
          runFilter={runFilter}
          caseFilter={caseFilter}
          providerFilter={providerFilter}
          severityFilter={severityFilter}
          paused={paused}
          speed={speed}
          onSetStageFilter={setStageFilter}
          onSetStatusFilter={setStatusFilter}
          onSetReasonFilter={setReasonFilter}
          onSetRunFilter={setRunFilter}
          onSetCaseFilter={setCaseFilter}
          onSetProviderFilter={setProviderFilter}
          onSetSeverityFilter={(value) => {
            setSeverityFilter(value === "error" || value === "warn" || value === "info" ? value : null);
          }}
          onTogglePause={() => {
            setFrozen(snapshot.trace);
            setPaused((value) => !value);
          }}
          onSetSpeed={setSpeed}
          {...(onReplayFixture !== undefined ? { onReplayFixture } : {})}
          {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
          {...(onRejectCandidate !== undefined ? { onRejectCandidate } : {})}
          {...(onSnoozeCandidate !== undefined ? { onSnoozeCandidate } : {})}
        />
      ) : null}
    </ScrollView>
  );
}

function OverviewPane({
  snapshot,
  onOpenLog,
  onStartSession,
  onEndSession,
}: {
  readonly snapshot: RelaySnapshot;
  readonly onOpenLog?: () => void;
  readonly onStartSession?: () => void;
  readonly onEndSession?: () => void;
}) {
  const runtime = snapshot.runtime;
  const review = snapshot.review;
  const execution = snapshot.caseExecution;
  return (
    <View style={styles.stack}>
      <Text style={styles.title}>Run inspector</Text>
      <Text style={styles.meta}>{`${runtime.runId} · ${runtime.commit} · ${runtime.storageAdapter}`}</Text>
      <Text style={styles.path}>{runtime.logPath || "No run folder"}</Text>
      <View style={styles.chips}>
        {snapshot.status.map((chip) => (
          <Text key={chip.id} style={styles.chip}>
            {`${chip.ok ? "●" : "○"} ${chip.label} ${chip.detail}`}
          </Text>
        ))}
      </View>
      <Text style={styles.meta}>
        {`session ${runtime.sessionId ?? "none"} · episode ${runtime.episodeId ?? "none"} · case ${runtime.activeCaseId ?? "none"}`}
      </Text>
      <Text style={styles.meta}>
        {`queue ${runtime.queueDepth} · deadLetters ${runtime.deadLetters} · mode ${runtime.mode}`}
      </Text>
      <Text style={styles.meta}>
        {runtime.logWritable
          ? `retention ${runtime.retention}`
          : `log error ${runtime.logError ?? "unavailable"}`}
      </Text>
      <View style={styles.row}>
        {onOpenLog ? (
          <Pressable onPress={onOpenLog} style={styles.button}>
            <Text style={styles.buttonText}>Open run folder</Text>
          </Pressable>
        ) : null}
        <Pressable onPress={onStartSession} style={styles.button}>
          <Text style={styles.buttonText}>Start session</Text>
        </Pressable>
        <Pressable onPress={onEndSession} style={styles.button}>
          <Text style={styles.buttonText}>End session</Text>
        </Pressable>
      </View>

      <Text style={styles.section}>Case execution</Text>
      {execution == null ? (
        <Text style={styles.empty}>No Case timing yet.</Text>
      ) : (
        <View style={styles.stack}>
          <Text style={styles.meta}>
            {`${execution.caseId}${execution.historical ? " · historical" : ""}`}
          </Text>
          {execution.steps.map((step) => (
            <Text key={`${execution.caseId}:${step.key}:${step.label}`} style={styles.trace}>
              {formatExecutionStep(step)}
            </Text>
          ))}
          <Text style={styles.trace}>{formatExecutionTotal(execution)}</Text>
        </View>
      )}

      <Text style={styles.section}>Self-review</Text>
      <Text style={styles.meta}>
        {`sessions ${review.completeSessions}/${review.sessionTrigger} · approved ${review.approvedCandidates} · built ${review.builtReflexes}/${review.reflexTrigger} · active ${review.activeReflexes} · episodes ${review.completeEpisodes}/${review.episodeTrigger} · candidates ${review.qualifiedCandidates}/${review.candidateTrigger}`}
      </Text>
      <Text style={styles.meta}>
        {review.reviewDue
          ? `Review due · ${review.trigger}. Recommendations only.`
          : "No review trigger yet."}
      </Text>
    </View>
  );
}

function LogsPane({
  snapshot,
  chronological,
  stageChips,
  statusChips,
  reasonChips,
  runChips,
  caseChips,
  providerChips,
  severityChips,
  stageFilter,
  statusFilter,
  reasonFilter,
  runFilter,
  caseFilter,
  providerFilter,
  severityFilter,
  paused,
  speed,
  onSetStageFilter,
  onSetStatusFilter,
  onSetReasonFilter,
  onSetRunFilter,
  onSetCaseFilter,
  onSetProviderFilter,
  onSetSeverityFilter,
  onTogglePause,
  onSetSpeed,
  onReplayFixture,
  onApproveCandidate,
  onRejectCandidate,
  onSnoozeCandidate,
}: {
  readonly snapshot: RelaySnapshot;
  readonly chronological: TraceRow[];
  readonly stageChips: string[];
  readonly statusChips: string[];
  readonly reasonChips: string[];
  readonly runChips: string[];
  readonly caseChips: string[];
  readonly providerChips: string[];
  readonly severityChips: string[];
  readonly stageFilter: string | null;
  readonly statusFilter: string | null;
  readonly reasonFilter: string | null;
  readonly runFilter: string | null;
  readonly caseFilter: string | null;
  readonly providerFilter: string | null;
  readonly severityFilter: ConsoleSeverity | null;
  readonly paused: boolean;
  readonly speed: number;
  readonly onSetStageFilter: (value: string | null) => void;
  readonly onSetStatusFilter: (value: string | null) => void;
  readonly onSetReasonFilter: (value: string | null) => void;
  readonly onSetRunFilter: (value: string | null) => void;
  readonly onSetCaseFilter: (value: string | null) => void;
  readonly onSetProviderFilter: (value: string | null) => void;
  readonly onSetSeverityFilter: (value: string | null) => void;
  readonly onTogglePause: () => void;
  readonly onSetSpeed: (value: number) => void;
  readonly onReplayFixture?: (fixture: string, speed: number) => void;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
}) {
  const [selectionText, setSelectionText] = useState<string | null>(null);
  return (
    <View style={styles.stack}>
      <Text style={styles.section}>Evidence</Text>
      {snapshot.patterns.length === 0 ? (
        <Text style={styles.empty}>No completed episodes yet.</Text>
      ) : (
        snapshot.patterns.map((pattern) => (
          <PatternCard
            key={pattern.signature}
            pattern={pattern}
            {...(onApproveCandidate !== undefined ? { onApproveCandidate } : {})}
            {...(onRejectCandidate !== undefined ? { onRejectCandidate } : {})}
            {...(onSnoozeCandidate !== undefined ? { onSnoozeCandidate } : {})}
          />
        ))
      )}

      <View style={styles.row}>
        <Text style={styles.section}>Timeline</Text>
        <Pressable onPress={onTogglePause} style={styles.button}>
          <Text style={styles.buttonText}>{paused ? "Resume" : "Pause"}</Text>
        </Pressable>
      </View>

      <FilterRow label="run" values={runChips} active={runFilter} onSelect={onSetRunFilter} />
      <FilterRow label="case" values={caseChips} active={caseFilter} onSelect={onSetCaseFilter} />
      <FilterRow label="provider" values={providerChips} active={providerFilter} onSelect={onSetProviderFilter} />
      <FilterRow label="severity" values={severityChips} active={severityFilter} onSelect={onSetSeverityFilter} />
      <FilterRow label="stage" values={stageChips} active={stageFilter} onSelect={onSetStageFilter} />
      <FilterRow label="status" values={statusChips} active={statusFilter} onSelect={onSetStatusFilter} />
      <FilterRow label="reason" values={reasonChips} active={reasonFilter} onSelect={onSetReasonFilter} />
      <View style={styles.row}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Copy run identifiers"
          onPress={() =>
            setSelectionText(
              copyableRunIds({
                runId: snapshot.runtime.runId,
                caseId: snapshot.runtime.activeCaseId,
                decisionId: snapshot.decision?.decisionId ?? null,
                sessionId: snapshot.runtime.sessionId,
              }),
            )
          }
          style={styles.button}
        >
          <Text style={styles.buttonText}>Copy IDs</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Export the filtered event selection"
          onPress={() => setSelectionText(exportTraceSelection(chronological))}
          style={styles.button}
        >
          <Text style={styles.buttonText}>Export selection</Text>
        </Pressable>
      </View>
      {selectionText ? (
        <Text selectable accessibilityLabel="Copied identifiers or exported selection" style={styles.trace}>
          {selectionText}
        </Text>
      ) : null}

      <Text style={styles.traceHeader}>{"time | +delta | duration | stage | status | case | episode | reason"}</Text>
      {chronological.length === 0 ? <Text style={styles.empty}>No canonical events yet.</Text> : null}
      {[...chronological].reverse().map((row, index, arr) => {
        const older = arr[index + 1];
        return (
          <Text key={row.sequence} style={styles.trace}>
            {formatTraceLine(row, older)}
          </Text>
        );
      })}

      <View style={styles.row}>
        <Text style={styles.meta}>glossary fixture</Text>
        {SPEEDS.map((value) => (
          <Pressable key={value} onPress={() => onSetSpeed(value)} style={styles.button}>
            <Text style={styles.buttonText}>{value === 0 ? "0×" : `${value}×`}</Text>
          </Pressable>
        ))}
        <Pressable onPress={() => onReplayFixture?.("acronym-basic", speed)} style={styles.button}>
          <Text style={styles.buttonText}>Replay</Text>
        </Pressable>
      </View>
    </View>
  );
}

function JudgmentEvidence({ attempts }: { readonly attempts: readonly DecisionAttemptView[] }) {
  if (attempts.length === 0) return null;
  return (
    <SideCard title="Judgment evidence">
      {attempts.map((attempt) => (
        <Text key={`${attempt.attempt}:${attempt.at}`} selectable style={styles.sideMeta}>
          {formatJudgmentAttempt(attempt)}
        </Text>
      ))}
    </SideCard>
  );
}

function formatJudgmentAttempt(attempt: DecisionAttemptView): string {
  const parts = [
    `#${attempt.attempt}`,
    attempt.status,
    attempt.reasonCode ?? "none",
    attempt.durationMs != null ? `${Math.round(attempt.durationMs)} ms` : null,
    attempt.httpStatus != null ? `http ${attempt.httpStatus}` : null,
    attempt.retryDelayMs != null ? `retry ${Math.round(attempt.retryDelayMs)} ms` : null,
    attempt.providerRequestId ? `request ${attempt.providerRequestId}` : "request omitted",
    attempt.disclosureGrantId ? `grant ${attempt.disclosureGrantId}` : null,
    attempt.grantScopeKind ? `scope ${attempt.grantScopeKind}` : null,
    attempt.grantExpiresAt ? `until ${attempt.grantExpiresAt}` : null,
    attempt.grantRequestsBefore != null && attempt.grantRequestsAfter != null
      ? `requests ${attempt.grantRequestsBefore} to ${attempt.grantRequestsAfter} of ${attempt.grantMaxRequests ?? "—"}`
      : null,
    attempt.grantBytesBefore != null && attempt.grantBytesAfter != null
      ? `bytes ${attempt.grantBytesBefore} to ${attempt.grantBytesAfter} of ${attempt.grantMaxBytes ?? "—"}`
      : null,
    attempt.disclosedSourceCount != null ? `sources ${attempt.disclosedSourceCount}` : null,
    attempt.disclosedBytes != null ? `disclosed ${attempt.disclosedBytes} bytes` : null,
  ];
  return parts.filter((part) => part != null).join(" · ");
}

function GateDetails({ gate }: { readonly gate: DecisionReceiptView | null }) {
  if (!gate) {
    return (
      <SideCard title="Gate receipt">
        <Text style={styles.empty}>No receipt yet.</Text>
      </SideCard>
    );
  }
  return (
    <SideCard title="Gate receipt">
      <Text style={styles.sideBody}>{`${gate.gateId} · ${gate.policyVersion}`}</Text>
      <Text style={styles.sideMeta}>{`${gate.result} · ${gate.reasonCode} · ${gate.nextAction}`}</Text>
      <Text style={styles.sideMeta}>{`provider ${gate.provider} · retries ${gate.retries}`}</Text>
      {Object.keys(gate.optionLabels).length > 0 ? (
        <Text style={styles.sideMeta}>{formatStringMap(gate.optionLabels)}</Text>
      ) : null}
      <Text style={styles.sideMeta}>{formatMap(gate.probabilities)}</Text>
    </SideCard>
  );
}

function SideCard({ title, children }: { readonly title: string; readonly children: ReactNode }) {
  return (
    <View style={styles.sideCard}>
      <Text style={styles.sideTitle}>{title}</Text>
      {children}
    </View>
  );
}

function PatternCard({
  pattern,
  onApproveCandidate,
  onRejectCandidate,
  onSnoozeCandidate,
}: {
  readonly pattern: PatternView;
  readonly onApproveCandidate?: (candidateId: string) => void;
  readonly onRejectCandidate?: (candidateId: string) => void;
  readonly onSnoozeCandidate?: (candidateId: string) => void;
}) {
  const rejected = rejectedOutcomes(pattern.outcomes);
  const showActions = pattern.candidateState === "proposed" && pattern.candidateId != null;
  const candidateId = pattern.candidateId ?? "";

  return (
    <View style={styles.card}>
      <Text style={styles.signature}>{pattern.signature}</Text>
      <Line label="Count / sessions" value={`${pattern.count} / ${pattern.sessions}`} />
      {rejected ? <Line label="Rejected outcomes" value={rejected} /> : null}
      <Line label="State" value={pattern.candidateState ?? "observing"} />
      <Line label="Needed" value={pattern.needed || "none"} />
      <Line label="Because" value={pattern.because || "not proposed"} />
      {showActions ? (
        <View style={styles.row}>
          <Pressable onPress={() => onApproveCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Approve</Text>
          </Pressable>
          <Pressable onPress={() => onRejectCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Reject</Text>
          </Pressable>
          <Pressable onPress={() => onSnoozeCandidate?.(candidateId)} style={styles.button}>
            <Text style={styles.buttonText}>Snooze</Text>
          </Pressable>
        </View>
      ) : null}
    </View>
  );
}

function FilterRow({
  label,
  values,
  active,
  onSelect,
}: {
  readonly label: string;
  readonly values: readonly string[];
  readonly active: string | null;
  readonly onSelect: (value: string | null) => void;
}) {
  if (values.length === 0) return null;
  return (
    <View style={styles.row}>
      <Text style={styles.meta}>{label}</Text>
      <Pressable
        onPress={() => onSelect(null)}
        style={[styles.button, active === null ? styles.buttonActive : null]}
      >
        <Text style={styles.buttonText}>all</Text>
      </Pressable>
      {values.map((value) => (
        <Pressable
          key={value}
          onPress={() => onSelect(active === value ? null : value)}
          style={[styles.button, active === value ? styles.buttonActive : null]}
        >
          <Text style={styles.buttonText}>{value}</Text>
        </Pressable>
      ))}
    </View>
  );
}

function Line({ label, value }: { readonly label: string; readonly value: string }) {
  return <Text style={styles.line}>{`${label}: ${value}`}</Text>;
}

function receivedRequestText(snapshot: RelaySnapshot): string | null {
  const caseId = snapshot.decision?.caseId ?? snapshot.caseExecution?.caseId ?? null;
  const asks = snapshot.feedItems.filter((item) => item.kind === "ask");
  const matched = caseId ? asks.filter((item) => item.caseId === caseId) : asks;
  const latest = matched.at(-1) ?? asks.at(-1);
  const text = latest?.summary.trim();
  if (text) return text;
  const preview = snapshot.currentInputPreview?.trim();
  return preview || null;
}

function hasJudgmentEvidence(snapshot: RelaySnapshot): boolean {
  if (snapshot.decision?.judgmentId) return true;
  return snapshot.trace.some(
    (row) =>
      (row.stage?.startsWith("judgment") ?? false) ||
      (row.type?.startsWith("judgment.") ?? false) ||
      Boolean(row.judgmentId),
  );
}

function formatJson(value: Readonly<Record<string, string | number | boolean | null>>): string {
  return JSON.stringify(value, null, 2);
}

function formatExecutionStep(step: CaseExecutionStepView): string {
  const timing =
    step.state === "skipped"
      ? "skipped"
      : step.durationMs != null
        ? `${Math.round(step.durationMs)} ms`
        : step.deltaMs == null
          ? "-"
          : step.deltaMs === 0
            ? "0 ms"
            : `+${Math.round(step.deltaMs)} ms`;
  return `${padLabel(step.label)}${timing}`;
}

function formatExecutionTotal(execution: CaseExecutionView): string {
  if (execution.outcome === "answered" && execution.totalMs != null) {
    return `${padLabel("Total")}${Math.round(execution.totalMs)} ms`;
  }
  if (execution.outcome === "resolved_without_answer") {
    return `${padLabel("Total")}resolved without answer`;
  }
  if (execution.outcome === "failed") return `${padLabel("Total")}failed`;
  if (execution.outcome === "blocked") return `${padLabel("Total")}blocked`;
  if (execution.outcome === "in_progress") return `${padLabel("Total")}in progress`;
  return `${padLabel("Total")}-`;
}

function padLabel(label: string): string {
  return `${label.padEnd(22, " ")}`;
}

function formatTraceLine(row: TraceRow, previous: TraceRow | undefined): string {
  const time = formatTime(row.at);
  const delta =
    previous != null ? `+${Math.max(0, Date.parse(row.at) - Date.parse(previous.at))}ms` : "+0ms";
  const duration = row.durationMs != null ? `${row.durationMs}ms` : "-";
  const stage = row.stage ?? "-";
  const status = row.status ?? "-";
  const caseId = row.caseId ?? "-";
  const episode = row.episodeId ?? "-";
  const reason = row.reasonCode ?? "-";
  return `${time} | ${delta} | ${duration} | ${stage} | ${status} | ${caseId} | ${episode} | ${reason}`;
}

function formatTime(at: string): string {
  const ms = Date.parse(at);
  if (Number.isNaN(ms)) return at;
  return new Date(ms).toISOString().slice(11, 23);
}

function formatMap(values: Readonly<Record<string, number>>): string {
  const entries = Object.entries(values);
  if (entries.length === 0) return "none";
  return entries.map(([key, value]) => `${key} ${value.toFixed(2)}`).join(" · ");
}

function formatStringMap(values: Readonly<Record<string, string>>): string {
  const entries = Object.entries(values);
  if (entries.length === 0) return "none";
  return entries.map(([key, value]) => `${key}=${value}`).join(" · ");
}

function rejectedOutcomes(outcomes: Readonly<Record<string, number>>): string | null {
  const rejected = Object.entries(outcomes).filter(
    ([key, count]) => count > 0 && /reject|fail|deny/i.test(key),
  );
  if (rejected.length === 0) return null;
  return rejected.map(([key, count]) => `${key} ${count}`).join(" · ");
}

function uniqueValues(values: readonly (string | null)[]): string[] {
  return [...new Set(values.filter((value): value is string => value != null && value !== ""))];
}

const styles = StyleSheet.create({
  panel: { flex: 1, backgroundColor: colors.console, minWidth: 0 },
  content: { padding: 18, gap: 12 },
  tabs: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 4,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    paddingBottom: 0,
  },
  tab: {
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderBottomWidth: 2,
    borderBottomColor: "transparent",
  },
  tabActive: { borderBottomColor: colors.cyan },
  tabText: { color: colors.textMuted, fontSize: 13, fontWeight: "600" },
  tabTextActive: { color: colors.cyan },
  stack: { gap: 8 },
  jevLayout: { flexDirection: "row", flexWrap: "wrap", gap: 14, alignItems: "flex-start" },
  caseLayout: { gap: 14 },
  jevMain: { flexGrow: 1, flexBasis: 420, minWidth: 0 },
  jevSide: { flexGrow: 1, flexBasis: 260, minWidth: 0, gap: 10 },
  sideCard: {
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 10,
    backgroundColor: colors.bgPanel,
    padding: 10,
    gap: 4,
  },
  sideTitle: { color: colors.textMuted, fontSize: 11, fontWeight: "700", textTransform: "uppercase" },
  sideBody: { color: colors.text, fontSize: 12 },
  sideMeta: { color: colors.textDim, fontSize: 11 },
  code: { color: colors.cyan, fontSize: 11, fontFamily: "monospace" },
  traceMini: { color: colors.textMuted, fontSize: 11, fontFamily: "monospace" },
  footerOk: { color: colors.ok, fontSize: 12, fontWeight: "600", marginTop: 4 },
  footerWait: { color: colors.warn, fontSize: 12, fontWeight: "600", marginTop: 4 },
  title: { color: colors.text, fontSize: 22, fontWeight: "700" },
  section: { color: colors.text, fontSize: 14, fontWeight: "700", marginTop: 8 },
  meta: { color: colors.textMuted, fontSize: 12 },
  path: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  chip: { color: colors.textMuted, fontSize: 12 },
  row: { flexDirection: "row", flexWrap: "wrap", gap: 8, alignItems: "center" },
  button: {
    borderWidth: 1,
    borderColor: colors.borderStrong,
    borderRadius: 8,
    paddingHorizontal: 8,
    minHeight: touchTarget,
    justifyContent: "center",
  },
  buttonActive: { borderColor: colors.cyan, backgroundColor: colors.accentSoft },
  buttonText: { color: colors.text, fontSize: 12 },
  card: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: 8, gap: 2 },
  signature: { color: colors.cyan, fontSize: 12, fontFamily: "monospace" },
  line: { color: colors.text, fontSize: 12 },
  empty: { color: colors.textDim, fontSize: 12 },
  traceHeader: { color: colors.textMuted, fontSize: 11, fontFamily: "monospace", marginTop: 4 },
  trace: { color: colors.text, fontSize: 12, fontFamily: "monospace" },
});
