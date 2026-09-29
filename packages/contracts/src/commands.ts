import type { ConnectorActionRef, ConnectorRef, ReflexRef } from "./artifacts.js";
import type { EventEnvelope, ObservationBinding, ScopedActionGrant } from "./events.js";
import type { EpisodeClassification, RawActivitySignal, WorkflowHostStatus } from "./workflow.js";

export type SubmitTextCommand = {
  readonly type: "SubmitText";
  readonly text: string;
  readonly caseId?: string;
};

export type SetListeningCommand = {
  readonly type: "SetListening";
  readonly enabled: boolean;
};

export type SetHostedProcessingCommand = {
  readonly type: "SetHostedProcessing";
  readonly enabled: boolean;
};

/** Defaults for the settings control that grants the current session. */
export const SESSION_JEV_GRANT_DEFAULTS = {
  scopeKind: "session",
  ttlMs: 12 * 60 * 60 * 1000,
  allowedSourceClasses: [
    "conversation_excerpt",
    "ambient_transcript",
    "claim_excerpt",
    "pattern_count",
  ],
  maxRequests: 20,
  maxBytes: 80_000,
} as const;

export type GrantJevDisclosureCommand = {
  readonly type: "GrantJevDisclosure";
  readonly scopeKind: "session" | "project";
  readonly scopeId?: string;
  readonly ttlMs: number;
  readonly allowedSourceClasses: readonly string[];
  readonly maxRequests: number;
  readonly maxBytes: number;
};

export type RevokeJevDisclosureCommand = {
  readonly type: "RevokeJevDisclosure";
  readonly grantId: string;
};

export type RefreshProviderHealthCommand = {
  readonly type: "RefreshProviderHealth";
};

export type UpsertGlossaryEntryCommand = {
  readonly type: "UpsertGlossaryEntry";
  readonly token: string;
  readonly expansion: string;
  readonly confirmed: boolean;
  readonly replace?: boolean;
};

export type CaptureBirthdayCommand = {
  readonly type: "CaptureBirthday";
  readonly displayName: string;
  readonly date: string;
  readonly confirmed: boolean;
  readonly replace?: boolean;
};

export type DeleteMemoryCommand = {
  readonly type: "DeleteMemory";
  readonly kind: "glossary" | "birthday" | "note" | "fact" | "recommendation";
  readonly key: string;
};

export type AcceptAmbientRecommendationCommand = {
  readonly type: "AcceptAmbientRecommendation";
  readonly recommendationId: string;
};

export type DismissAmbientRecommendationCommand = {
  readonly type: "DismissAmbientRecommendation";
  readonly recommendationId: string;
};

export type FeedbackAmbientRecommendationCommand = {
  readonly type: "FeedbackAmbientRecommendation";
  readonly recommendationId: string;
  readonly feedback: "not_useful" | "never_for_project" | "why";
};

export type ApproveCandidateCommand = {
  readonly type: "ApproveCandidate";
  readonly candidateId: string;
};

export type RejectCandidateCommand = {
  readonly type: "RejectCandidate";
  readonly candidateId: string;
};

export type SnoozeCandidateCommand = {
  readonly type: "SnoozeCandidate";
  readonly candidateId: string;
};

export type StartWorkSessionCommand = {
  readonly type: "StartWorkSession";
};

export type EndWorkSessionCommand = {
  readonly type: "EndWorkSession";
};

export type ApproveOperationCommand = {
  readonly type: "ApproveOperation";
  readonly operationId: string;
  readonly expectedCanonicalHash: string;
  readonly expectedCaseVersion: number;
};

export type RejectOperationCommand = {
  readonly type: "RejectOperation";
  readonly operationId: string;
  readonly expectedCanonicalHash: string;
  readonly expectedCaseVersion: number;
  readonly reason?: string;
};

export type StartConnectionAuthorizationCommand = {
  readonly type: "StartConnectionAuthorization";
  readonly connector: ConnectorRef;
};

export type CompleteConnectionAuthorizationCommand = {
  readonly type: "CompleteConnectionAuthorization";
  readonly authorizationAttemptId: string;
  readonly providerCallbackRef: string;
};

export type DiscoverConnectionResourcesCommand = {
  readonly type: "DiscoverConnectionResources";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
};

export type SelectConnectionResourcesCommand = {
  readonly type: "SelectConnectionResources";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly selectedResourceIds: readonly string[];
};

export type StartConnectionReauthorizationCommand = {
  readonly type: "StartConnectionReauthorization";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly additionalOAuthScopes: readonly string[];
};

export type CompleteConnectionReauthorizationCommand = {
  readonly type: "CompleteConnectionReauthorization";
  readonly authorizationAttemptId: string;
  readonly providerCallbackRef: string;
};

export type SetConnectionObservationCommand = {
  readonly type: "SetConnectionObservation";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly observationEnabled: boolean;
};

export type SetWriteActionCommand = {
  readonly type: "SetWriteAction";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly action: ConnectorActionRef;
  readonly enabled: boolean;
};

export type GrantHostedDisclosureCommand = {
  readonly type: "GrantHostedDisclosure";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly disclosure: "hosted_session" | "hosted_project" | "public";
  readonly sensitivity: number;
  readonly purpose: string;
  readonly ttlMs?: number;
};

export type RevokeHostedDisclosureCommand = {
  readonly type: "RevokeHostedDisclosure";
  readonly grantId: string;
  readonly expectedGrantVersion: number;
};

export type RevokeConnectionCredentialsCommand = {
  readonly type: "RevokeConnectionCredentials";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
};

export type DisconnectConnectionCommand = {
  readonly type: "DisconnectConnection";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
};

export type DeleteImportedConnectionContentCommand = {
  readonly type: "DeleteImportedConnectionContent";
  readonly connectionId: string;
  readonly expectedConnectionVersion: number;
  readonly confirmationToken: string;
};

export type ActivateReflexCommand = {
  readonly type: "ActivateReflex";
  readonly reflex: ReflexRef;
  readonly expectedStateVersion: number;
};

export type PauseReflexCommand = {
  readonly type: "PauseReflex";
  readonly reflex: ReflexRef;
  readonly expectedStateVersion: number;
};

export type RollbackReflexCommand = {
  readonly type: "RollbackReflex";
  readonly reflex: ReflexRef;
  readonly expectedStateVersion: number;
};

export type ReplayFixtureCommand = {
  readonly type: "ReplayFixture";
  readonly fixturePath: string;
  readonly speed: number;
};

export type CancelActiveCommand = {
  readonly type: "CancelActive";
};

export type IngestObservedEventCommand = {
  readonly type: "IngestObservedEvent";
  readonly envelope: EventEnvelope;
};

export type DecideVerifyCommand = {
  readonly type: "DecideVerify";
  readonly verifyId: string;
  readonly decision: "accept" | "dismiss" | "correct";
  readonly correction?: string;
};

export type RenameProjectCaseCommand = {
  readonly type: "RenameProjectCase";
  readonly projectCaseId: string;
  readonly alias: string;
  readonly expectedVersion: number;
};

export type BindObservationCommand = {
  readonly type: "BindObservation";
  readonly binding: ObservationBinding;
};

export type SetScopedGrantCommand = {
  readonly type: "SetScopedGrant";
  readonly grant: ScopedActionGrant;
};

export type WorkflowCommand =
  | {
      readonly type: "Workflow";
      readonly action: "configure";
      readonly setupComplete?: boolean;
      readonly paused?: boolean;
      readonly windowsEnabled?: boolean;
      readonly chromeEnabled?: boolean;
      readonly pageContentEnabled?: boolean;
      readonly retentionDays?: number;
      readonly allowedSites?: readonly string[];
      readonly allowedFolders?: readonly string[];
      readonly allowSessionSite?: string;
    }
  | { readonly type: "Workflow"; readonly action: "ingest"; readonly signals: readonly RawActivitySignal[] }
  | {
      readonly type: "Workflow";
      readonly action: "correct";
      readonly episodeId: string;
      readonly classification?: EpisodeClassification;
      readonly startedAt?: string;
      readonly endedAt?: string;
      readonly company?: string;
      readonly position?: string;
    }
  | { readonly type: "Workflow"; readonly action: "assign_case"; readonly episodeId: string; readonly caseId: string | null }
  | { readonly type: "Workflow"; readonly action: "ensure_job_case" }
  | {
      readonly type: "Workflow";
      readonly action: "decide_proposal";
      readonly proposalId: string;
      readonly decision: "accept" | "reject" | "suppress";
    }
  | { readonly type: "Workflow"; readonly action: "preview_draft"; readonly episodeId: string }
  | { readonly type: "Workflow"; readonly action: "run_draft"; readonly episodeId: string }
  | { readonly type: "Workflow"; readonly action: "activate_reflex" }
  | { readonly type: "Workflow"; readonly action: "pause_reflex" }
  | {
      readonly type: "Workflow";
      readonly action: "record_outcome";
      readonly episodeId: string;
      readonly outcome: "submitted" | "in_progress" | "research";
    }
  | {
      readonly type: "Workflow";
      readonly action: "record_correction";
      readonly episodeId: string;
      readonly before: string;
      readonly after: string;
    }
  | {
      readonly type: "Workflow";
      readonly action: "decide_improvement";
      readonly proposalId: string;
      readonly decision: "accept" | "reject" | "suppress" | "edit";
      readonly preference?: string;
    }
  | { readonly type: "Workflow"; readonly action: "delete_observations" }
  | { readonly type: "Workflow"; readonly action: "host_status"; readonly host: WorkflowHostStatus }
  | { readonly type: "Workflow"; readonly action: "set_master_resume"; readonly path: string }
  | { readonly type: "Workflow"; readonly action: "judge_episode"; readonly episodeId: string };

export type RelayCommand =
  | SubmitTextCommand
  | SetListeningCommand
  | SetHostedProcessingCommand
  | GrantJevDisclosureCommand
  | RevokeJevDisclosureCommand
  | RefreshProviderHealthCommand
  | UpsertGlossaryEntryCommand
  | CaptureBirthdayCommand
  | DeleteMemoryCommand
  | AcceptAmbientRecommendationCommand
  | DismissAmbientRecommendationCommand
  | FeedbackAmbientRecommendationCommand
  | ApproveCandidateCommand
  | RejectCandidateCommand
  | SnoozeCandidateCommand
  | StartWorkSessionCommand
  | EndWorkSessionCommand
  | ApproveOperationCommand
  | RejectOperationCommand
  | StartConnectionAuthorizationCommand
  | CompleteConnectionAuthorizationCommand
  | DiscoverConnectionResourcesCommand
  | SelectConnectionResourcesCommand
  | StartConnectionReauthorizationCommand
  | CompleteConnectionReauthorizationCommand
  | SetConnectionObservationCommand
  | SetWriteActionCommand
  | GrantHostedDisclosureCommand
  | RevokeHostedDisclosureCommand
  | RevokeConnectionCredentialsCommand
  | DisconnectConnectionCommand
  | DeleteImportedConnectionContentCommand
  | ActivateReflexCommand
  | PauseReflexCommand
  | RollbackReflexCommand
  | ReplayFixtureCommand
  | CancelActiveCommand
  | IngestObservedEventCommand
  | DecideVerifyCommand
  | RenameProjectCaseCommand
  | BindObservationCommand
  | SetScopedGrantCommand
  | WorkflowCommand;

export type RelayCommandResult = {
  readonly ok: boolean;
  readonly summary: string;
  readonly caseId?: string;
  readonly operationId?: string;
  readonly connectionId?: string;
  readonly authorizationAttemptId?: string;
  readonly reflexId?: string;
  readonly reflexVersion?: number;
  readonly error?: string;
};
