using Relay.Core.Connectors;
using Relay.Core.Security;

namespace Relay.Connectors;

/// <summary>
/// Complete versioned alpha connector definitions. Every write defaults off.
/// Observed content is stored local-only; hosted disclosure requires a separate grant.
/// Provider SDK implementations are not registered here yet.
/// </summary>
public static class ConnectorCatalog
{
    public static readonly string[] GitHubReadScopes =
    [
        "permissions:metadata:read",
        "permissions:contents:read",
        "permissions:issues:read",
        "permissions:pull_requests:read",
    ];

    public static readonly string[] GitHubIssueWriteScopes =
    [
        "permissions:issues:write",
    ];

    public static IReadOnlyList<ConnectorDefinition> AlphaConnectors { get; } =
    [
        Conversation(),
        Memory(),
        GoogleCalendar(),
        Gmail(),
        GoogleSheets(),
        GitHub(),
        PublicWeb(),
        Wikipedia(),
        Plaid(),
    ];

    public static ConnectorDefinition Require(string id, int version = 1) =>
        AlphaConnectors.Single(c => c.Id == id && c.Version == version);

    public static bool TryGetAction(string connectorId, int connectorVersion, string actionId, int actionVersion, out ConnectorOperationDefinition? operation)
    {
        var connector = AlphaConnectors.FirstOrDefault(c => c.Id == connectorId && c.Version == connectorVersion);
        if (connector is null)
        {
            operation = null;
            return false;
        }

        operation = connector.Operations.FirstOrDefault(o => o.ActionId == actionId && o.ActionVersion == actionVersion);
        return operation is not null;
    }

    private static ConnectorDefinition Conversation() => new(
        Id: "conversation",
        Version: 1,
        DisplayName: "Local conversation",
        SupportsObservation: true,
        DefaultReadScopes: ["ask", "listen"],
        Operations:
        [
            Read("conversation.search", ConnectorSchemas.SearchQuery, DataPolicy.Observed(DataSensitivity.Personal)),
            Write(
                "conversation.note-save",
                ConnectorSchemas.NoteBody,
                DataPolicy.Observed(DataSensitivity.Personal),
                RiskClass.Low,
                supportsIdempotency: true,
                supportsReconciliation: false,
                requiresResourceScope: false),
        ],
        DefaultRateLimit: null);

    private static ConnectorDefinition Memory() => new(
        Id: "memory",
        Version: 1,
        DisplayName: "Local RELAY memory",
        SupportsObservation: false,
        DefaultReadScopes: ["notes", "acronyms"],
        Operations:
        [
            Read("memory.search", ConnectorSchemas.SearchQuery, DataPolicy.LocalOnly),
            Read("memory.note-get", ConnectorSchemas.ResourceId, DataPolicy.LocalOnly),
            Write(
                "memory.note-create",
                ConnectorSchemas.NoteBody,
                DataPolicy.LocalOnly,
                RiskClass.Low,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: false),
            Write(
                "memory.acronym-remember",
                ConnectorSchemas.AcronymRemember,
                DataPolicy.LocalOnly,
                RiskClass.Low,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: false),
        ],
        DefaultRateLimit: null);

    private static ConnectorDefinition GoogleCalendar()
    {
        var observed = DataPolicy.Observed(DataSensitivity.Personal | DataSensitivity.Calendar);
        return new(
            Id: "google-calendar",
            Version: 1,
            DisplayName: "Google Calendar",
            SupportsObservation: true,
            DefaultReadScopes: ["calendars.selected"],
            Operations:
            [
                Observe("google-calendar.observe", observed, ["https://www.googleapis.com/auth/calendar.readonly"]),
                Read("google-calendar.events-list", ConnectorSchemas.ResourceId, observed, ["https://www.googleapis.com/auth/calendar.readonly"], requiresResourceScope: true),
                Read("google-calendar.events-search", ConnectorSchemas.SearchQuery, observed, ["https://www.googleapis.com/auth/calendar.readonly"], requiresResourceScope: true),
                Write(
                    "google-calendar.event-create",
                    ConnectorSchemas.CalendarEventCreate,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: ["https://www.googleapis.com/auth/calendar.events"]),
                Write(
                    "google-calendar.event-update",
                    ConnectorSchemas.CalendarEventUpdate,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: ["https://www.googleapis.com/auth/calendar.events"]),
            ],
            DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));
    }

    private static ConnectorDefinition Gmail()
    {
        var observed = DataPolicy.Observed(DataSensitivity.Personal | DataSensitivity.Email);
        return new(
            Id: "gmail",
            Version: 1,
            DisplayName: "Gmail",
            SupportsObservation: true,
            DefaultReadScopes: ["labels.selected"],
            Operations:
            [
                Observe("gmail.observe", observed, ["https://www.googleapis.com/auth/gmail.readonly"]),
                Read("gmail.messages-list", ConnectorSchemas.ResourceId, observed, ["https://www.googleapis.com/auth/gmail.readonly"], requiresResourceScope: true),
                Read("gmail.messages-search", ConnectorSchemas.SearchQuery, observed, ["https://www.googleapis.com/auth/gmail.readonly"], requiresResourceScope: true),
                Write(
                    "gmail.draft-create",
                    ConnectorSchemas.GmailDraftCreate,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: ["https://www.googleapis.com/auth/gmail.compose"]),
            ],
            DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));
    }

    private static ConnectorDefinition GoogleSheets()
    {
        var observed = DataPolicy.Observed(DataSensitivity.Personal);
        return new(
            Id: "google-sheets",
            Version: 1,
            DisplayName: "Google Sheets",
            SupportsObservation: true,
            DefaultReadScopes: ["spreadsheets.selected"],
            Operations:
            [
                Observe("google-sheets.observe", observed, ["https://www.googleapis.com/auth/spreadsheets.readonly"]),
                Read("google-sheets.values-get", ConnectorSchemas.ResourceId, observed, ["https://www.googleapis.com/auth/spreadsheets.readonly"], requiresResourceScope: true),
                Write(
                    "google-sheets.values-append",
                    ConnectorSchemas.SheetValuesWrite,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: ["https://www.googleapis.com/auth/spreadsheets"]),
                Write(
                    "google-sheets.values-upsert",
                    ConnectorSchemas.SheetValuesWrite,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: ["https://www.googleapis.com/auth/spreadsheets"]),
            ],
            DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));
    }

    private static ConnectorDefinition GitHub()
    {
        var observed = DataPolicy.Observed(DataSensitivity.SourceCode);
        return new(
            Id: "github",
            Version: 1,
            DisplayName: "GitHub",
            SupportsObservation: true,
            DefaultReadScopes: ["repositories.selected"],
            Operations:
            [
                Observe("github.observe", observed, GitHubReadScopes),
                Read("github.issues-list", ConnectorSchemas.ResourceId, observed, GitHubReadScopes, requiresResourceScope: true),
                Read("github.issues-search", ConnectorSchemas.SearchQuery, observed, GitHubReadScopes, requiresResourceScope: true),
                Read("github.pull-requests-search", ConnectorSchemas.SearchQuery, observed, GitHubReadScopes, requiresResourceScope: true),
                Read("github.code-search", ConnectorSchemas.SearchQuery, observed, GitHubReadScopes, requiresResourceScope: true),
                Read("github.content-get", ConnectorSchemas.ResourceId, observed, GitHubReadScopes, requiresResourceScope: true),
                Read("github.comments-list", ConnectorSchemas.ResourceId, observed, GitHubReadScopes, requiresResourceScope: true),
                Write(
                    "github.issue-draft-local",
                    ConnectorSchemas.GitHubLocalDraft,
                    DataPolicy.LocalOnly with { Sensitivity = DataSensitivity.SourceCode },
                    RiskClass.Low,
                    supportsIdempotency: true,
                    supportsReconciliation: false,
                    requiresResourceScope: false,
                    oauth: []),
                Write(
                    "github.comment-draft-local",
                    ConnectorSchemas.GitHubLocalDraft,
                    DataPolicy.LocalOnly with { Sensitivity = DataSensitivity.SourceCode },
                    RiskClass.Low,
                    supportsIdempotency: true,
                    supportsReconciliation: false,
                    requiresResourceScope: false,
                    oauth: []),
                Write(
                    "github.issue-create",
                    ConnectorSchemas.GitHubIssueCreate,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: GitHubIssueWriteScopes),
                Write(
                    "github.issue-comment-create",
                    ConnectorSchemas.GitHubCommentCreate,
                    observed,
                    RiskClass.Medium,
                    supportsIdempotency: true,
                    supportsReconciliation: true,
                    requiresResourceScope: true,
                    oauth: GitHubIssueWriteScopes),
            ],
            DefaultRateLimit: new RateLimitPolicy(30, TimeSpan.FromMinutes(1)));
    }

    private static ConnectorDefinition PublicWeb() => new(
        Id: "public-web",
        Version: 1,
        DisplayName: "Public web search",
        SupportsObservation: false,
        DefaultReadScopes: ["search"],
        Operations:
        [
            Read("public-web.search", ConnectorSchemas.SearchQuery, DataPolicy.Public),
            Read("public-web.fetch", ConnectorSchemas.ResourceId, DataPolicy.Public),
        ],
        DefaultRateLimit: new RateLimitPolicy(30, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition Wikipedia() => new(
        Id: "wikipedia",
        Version: 1,
        DisplayName: "Wikipedia",
        SupportsObservation: false,
        DefaultReadScopes: ["search"],
        Operations:
        [
            Read("wikipedia.search", ConnectorSchemas.SearchQuery, DataPolicy.Public),
            Read("wikipedia.page-get", ConnectorSchemas.ResourceId, DataPolicy.Public),
        ],
        DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition Plaid()
    {
        var financial = DataPolicy.Observed(DataSensitivity.Financial);
        return new(
            Id: "plaid",
            Version: 1,
            DisplayName: "Plaid",
            SupportsObservation: true,
            DefaultReadScopes: ["accounts.selected"],
            Operations:
            [
                Observe("plaid.observe", financial, ["transactions"]),
                Read("plaid.transactions-list", ConnectorSchemas.ResourceId, financial, ["transactions"], requiresResourceScope: true),
            ],
            DefaultRateLimit: new RateLimitPolicy(30, TimeSpan.FromMinutes(1)));
    }

    private static ConnectorOperationDefinition Observe(
        string actionId,
        DataPolicy policy,
        IReadOnlyList<string> oauth) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Observe,
            InputPolicy: policy,
            ReturnedPolicy: policy,
            InputSchema: ConnectorSchemas.Ref(ConnectorSchemas.EmptyObject),
            OutputSchema: ConnectorSchemas.Ref(ConnectorSchemas.ObservedItemArray),
            RequiredOAuthScopes: oauth,
            RiskClass: RiskClass.None,
            SupportsIdempotency: false,
            SupportsReconciliation: false,
            DefaultEnabled: true,
            RequiresResourceScope: true,
            RateLimit: null);

    private static ConnectorOperationDefinition Read(
        string actionId,
        JsonSchemaDocument inputSchema,
        DataPolicy policy,
        IReadOnlyList<string>? oauth = null,
        bool requiresResourceScope = false) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Read,
            InputPolicy: policy,
            ReturnedPolicy: policy,
            InputSchema: ConnectorSchemas.Ref(inputSchema),
            OutputSchema: ConnectorSchemas.Ref(ConnectorSchemas.ObservedItemArray),
            RequiredOAuthScopes: oauth ?? [],
            RiskClass: RiskClass.None,
            SupportsIdempotency: false,
            SupportsReconciliation: false,
            DefaultEnabled: true,
            RequiresResourceScope: requiresResourceScope,
            RateLimit: null);

    private static ConnectorOperationDefinition Write(
        string actionId,
        JsonSchemaDocument inputSchema,
        DataPolicy policy,
        RiskClass risk,
        bool supportsIdempotency,
        bool supportsReconciliation,
        bool requiresResourceScope,
        IReadOnlyList<string>? oauth = null) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Write,
            InputPolicy: policy,
            ReturnedPolicy: policy,
            InputSchema: ConnectorSchemas.Ref(inputSchema),
            OutputSchema: ConnectorSchemas.Ref(ConnectorSchemas.ExternalIdResult),
            RequiredOAuthScopes: oauth ?? [],
            RiskClass: risk,
            SupportsIdempotency: supportsIdempotency,
            SupportsReconciliation: supportsReconciliation,
            DefaultEnabled: false,
            RequiresResourceScope: requiresResourceScope,
            RateLimit: null);
}
