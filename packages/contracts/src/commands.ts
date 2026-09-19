import type { ConnectorActionRef, ConnectorRef, ReflexRef } from "./artifacts.js";

export type SubmitTextCommand = {
  readonly type: "SubmitText";
  readonly text: string;
  readonly caseId?: string;
};

export type SetListeningCommand = {
  readonly type: "SetListening";
  readonly enabled: boolean;
};

export type RememberTokenCommand = {
  readonly type: "RememberToken";
  readonly token: string;
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

export type ReplayFixtureCommand = {
  readonly type: "ReplayFixture";
  readonly fixturePath: string;
  readonly speed: number;
};

export type RelayCommand =
  | SubmitTextCommand
  | SetListeningCommand
  | RememberTokenCommand
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
  | ReplayFixtureCommand;

export type RelayCommandResult = {
  readonly ok: boolean;
  readonly summary: string;
  readonly caseId?: string;
  readonly operationId?: string;
  readonly connectionId?: string;
  readonly authorizationAttemptId?: string;
  readonly error?: string;
};
