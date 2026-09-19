using Relay.Core.Connectors;
using Relay.Core.Security;

namespace Relay.Connectors;

/// <summary>
/// Complete versioned alpha connector definitions. Every write defaults off.
/// Provider SDK implementations are not registered here yet.
/// </summary>
public static class ConnectorCatalog
{
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

    public static ConnectorDefinition Require(string id) =>
        AlphaConnectors.Single(c => c.Id == id);

    private static ConnectorDefinition Conversation() => new(
        Id: "conversation",
        Version: 1,
        DisplayName: "Local conversation",
        SupportsObservation: true,
        DefaultReadScopes: ["ask", "listen"],
        Operations:
        [
            Read("conversation.search", "Search recent conversation objects", DataClassification.HostedAllowedSession),
            Write(
                "conversation.note-save",
                "Save a local note from conversation",
                DataClassification.LocalOnly,
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
            Read("memory.search", "Search notes and acronyms", DataClassification.LocalOnly),
            Read("memory.note-get", "Get a note by id", DataClassification.LocalOnly),
            Write(
                "memory.note-create",
                "Create or supersede a local note",
                DataClassification.LocalOnly,
                RiskClass.Low,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: false),
            Write(
                "memory.acronym-remember",
                "Remember an acronym expansion",
                DataClassification.LocalOnly,
                RiskClass.Low,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: false),
        ],
        DefaultRateLimit: null);

    private static ConnectorDefinition GoogleCalendar() => new(
        Id: "google-calendar",
        Version: 1,
        DisplayName: "Google Calendar",
        SupportsObservation: true,
        DefaultReadScopes: ["calendars.selected"],
        Operations:
        [
            Observe("google-calendar.observe", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/calendar.readonly"]),
            Read("google-calendar.events-list", "List events in a selected calendar", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/calendar.readonly"], requiresResourceScope: true),
            Write(
                "google-calendar.event-create",
                "Create a calendar event",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["https://www.googleapis.com/auth/calendar.events"]),
            Write(
                "google-calendar.event-update",
                "Update a calendar event",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["https://www.googleapis.com/auth/calendar.events"]),
        ],
        DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition Gmail() => new(
        Id: "gmail",
        Version: 1,
        DisplayName: "Gmail",
        SupportsObservation: true,
        DefaultReadScopes: ["labels.selected"],
        Operations:
        [
            Observe("gmail.observe", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/gmail.readonly"]),
            Read("gmail.messages-list", "List messages in selected labels", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/gmail.readonly"], requiresResourceScope: true),
            Write(
                "gmail.draft-create",
                "Create a Gmail draft",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["https://www.googleapis.com/auth/gmail.compose"]),
        ],
        DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition GoogleSheets() => new(
        Id: "google-sheets",
        Version: 1,
        DisplayName: "Google Sheets",
        SupportsObservation: true,
        DefaultReadScopes: ["spreadsheets.selected"],
        Operations:
        [
            Observe("google-sheets.observe", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/spreadsheets.readonly"]),
            Read("google-sheets.values-get", "Read selected sheet ranges", DataClassification.HostedAllowedSession, ["https://www.googleapis.com/auth/spreadsheets.readonly"], requiresResourceScope: true),
            Write(
                "google-sheets.values-append",
                "Append rows to a selected range",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["https://www.googleapis.com/auth/spreadsheets"]),
            Write(
                "google-sheets.values-upsert",
                "Upsert rows in a selected range",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["https://www.googleapis.com/auth/spreadsheets"]),
        ],
        DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition GitHub() => new(
        Id: "github",
        Version: 1,
        DisplayName: "GitHub",
        SupportsObservation: true,
        DefaultReadScopes: ["repositories.selected"],
        Operations:
        [
            Observe("github.observe", DataClassification.HostedAllowedSession, ["repo"]),
            Read("github.issues-list", "List issues in selected repositories", DataClassification.HostedAllowedSession, ["repo"], requiresResourceScope: true),
            Write(
                "github.issue-draft",
                "Draft a GitHub issue",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["repo"]),
            Write(
                "github.comment-draft",
                "Draft a GitHub comment",
                DataClassification.HostedAllowedSession,
                RiskClass.Medium,
                supportsIdempotency: true,
                supportsReconciliation: true,
                requiresResourceScope: true,
                oauth: ["repo"]),
        ],
        DefaultRateLimit: new RateLimitPolicy(30, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition PublicWeb() => new(
        Id: "public-web",
        Version: 1,
        DisplayName: "Public web search",
        SupportsObservation: false,
        DefaultReadScopes: ["search"],
        Operations:
        [
            Read("public-web.search", "Search the public web", DataClassification.Public, requiresResourceScope: false),
            Read("public-web.fetch", "Fetch a public URL", DataClassification.Public, requiresResourceScope: false),
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
            Read("wikipedia.search", "Search Wikipedia", DataClassification.Public, requiresResourceScope: false),
            Read("wikipedia.page-get", "Fetch a Wikipedia page", DataClassification.Public, requiresResourceScope: false),
        ],
        DefaultRateLimit: new RateLimitPolicy(60, TimeSpan.FromMinutes(1)));

    private static ConnectorDefinition Plaid() => new(
        Id: "plaid",
        Version: 1,
        DisplayName: "Plaid",
        SupportsObservation: true,
        DefaultReadScopes: ["accounts.selected"],
        Operations:
        [
            Observe("plaid.observe", DataClassification.Financial, ["transactions"]),
            Read("plaid.transactions-list", "List transactions for selected accounts", DataClassification.Financial, ["transactions"], requiresResourceScope: true),
        ],
        DefaultRateLimit: new RateLimitPolicy(30, TimeSpan.FromMinutes(1)));

    private static ConnectorOperationDefinition Observe(
        string actionId,
        DataClassification classification,
        IReadOnlyList<string> oauth) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Observe,
            InputClassification: classification,
            ReturnedDataClassification: classification,
            InputSchema: "{\"type\":\"object\"}",
            OutputSchema: "{\"type\":\"array\",\"items\":{\"type\":\"object\"}}",
            RequiredOAuthScopes: oauth,
            RiskClass: RiskClass.None,
            SupportsIdempotency: false,
            SupportsReconciliation: false,
            DefaultEnabled: true,
            RequiresResourceScope: true,
            RateLimit: null);

    private static ConnectorOperationDefinition Read(
        string actionId,
        string description,
        DataClassification classification,
        IReadOnlyList<string>? oauth = null,
        bool requiresResourceScope = false) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Read,
            InputClassification: classification,
            ReturnedDataClassification: classification,
            InputSchema: "{\"type\":\"object\",\"description\":\"" + description + "\"}",
            OutputSchema: "{\"type\":\"array\",\"items\":{\"type\":\"object\"}}",
            RequiredOAuthScopes: oauth ?? [],
            RiskClass: RiskClass.None,
            SupportsIdempotency: false,
            SupportsReconciliation: false,
            DefaultEnabled: true,
            RequiresResourceScope: requiresResourceScope,
            RateLimit: null);

    private static ConnectorOperationDefinition Write(
        string actionId,
        string description,
        DataClassification classification,
        RiskClass risk,
        bool supportsIdempotency,
        bool supportsReconciliation,
        bool requiresResourceScope,
        IReadOnlyList<string>? oauth = null) =>
        new(
            ActionId: actionId,
            ActionVersion: 1,
            Kind: ConnectorOperationKind.Write,
            InputClassification: classification,
            ReturnedDataClassification: classification,
            InputSchema: "{\"type\":\"object\",\"description\":\"" + description + "\"}",
            OutputSchema: "{\"type\":\"object\",\"properties\":{\"externalId\":{\"type\":\"string\"}}}",
            RequiredOAuthScopes: oauth ?? [],
            RiskClass: risk,
            SupportsIdempotency: supportsIdempotency,
            SupportsReconciliation: supportsReconciliation,
            DefaultEnabled: false,
            RequiresResourceScope: requiresResourceScope,
            RateLimit: null);
}
