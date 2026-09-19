using Relay.Core.Connectors;

namespace Relay.Connectors;

/// <summary>Versioned JSON Schema documents referenced by connector operations.</summary>
public static class ConnectorSchemas
{
    public static readonly JsonSchemaDocument EmptyObject = Doc(
        "schema.empty-object",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false}""");

    public static readonly JsonSchemaDocument SearchQuery = Doc(
        "schema.search-query",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["query"],"properties":{"query":{"type":"string","minLength":1,"maxLength":512},"limit":{"type":"integer","minimum":1,"maximum":25}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument ResourceId = Doc(
        "schema.resource-id",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["resourceId"],"properties":{"resourceId":{"type":"string","minLength":1}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument NoteBody = Doc(
        "schema.note-body",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["title","body"],"properties":{"title":{"type":"string","minLength":1,"maxLength":200},"body":{"type":"string","minLength":1},"sourceObjectIds":{"type":"array","items":{"type":"string"}}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument AcronymRemember = Doc(
        "schema.acronym-remember",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["token","expansion"],"properties":{"token":{"type":"string","minLength":2,"maxLength":32},"expansion":{"type":"string","minLength":1,"maxLength":256}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument CalendarEventCreate = Doc(
        "schema.calendar-event-create",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["calendarId","title","startDate","recurrence"],"properties":{"calendarId":{"type":"string"},"title":{"type":"string","minLength":1},"startDate":{"type":"string","format":"date"},"recurrence":{"type":"string"},"reminderMinutes":{"type":"integer","minimum":0}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument CalendarEventUpdate = Doc(
        "schema.calendar-event-update",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["calendarId","eventId","title"],"properties":{"calendarId":{"type":"string"},"eventId":{"type":"string"},"title":{"type":"string","minLength":1},"startDate":{"type":"string","format":"date"},"reminderMinutes":{"type":"integer","minimum":0}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument GmailDraftCreate = Doc(
        "schema.gmail-draft-create",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["to","subject","body"],"properties":{"to":{"type":"array","items":{"type":"string","format":"email"},"minItems":1},"subject":{"type":"string"},"body":{"type":"string"}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument SheetValuesWrite = Doc(
        "schema.sheet-values-write",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["spreadsheetId","range","rows"],"properties":{"spreadsheetId":{"type":"string"},"range":{"type":"string"},"rows":{"type":"array","items":{"type":"array","items":{"type":["string","number","boolean","null"]}}}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument GitHubIssueCreate = Doc(
        "schema.github-issue-create",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["repository","title","body"],"properties":{"repository":{"type":"string"},"title":{"type":"string","minLength":1},"body":{"type":"string"}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument GitHubCommentCreate = Doc(
        "schema.github-comment-create",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["repository","issueNumber","body"],"properties":{"repository":{"type":"string"},"issueNumber":{"type":"integer","minimum":1},"body":{"type":"string","minLength":1}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument GitHubLocalDraft = Doc(
        "schema.github-local-draft",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["repository","title","body"],"properties":{"repository":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"},"issueNumber":{"type":"integer","minimum":1}},"additionalProperties":false}""");

    public static readonly JsonSchemaDocument ObservedItemArray = Doc(
        "schema.observed-item-array",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"array","items":{"type":"object","required":["providerItemId","providerRevision"],"properties":{"providerItemId":{"type":"string"},"providerRevision":{"type":"string"},"deleted":{"type":"boolean"}}}}""");

    public static readonly JsonSchemaDocument ExternalIdResult = Doc(
        "schema.external-id-result",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"externalId":{"type":"string"},"externalRevision":{"type":"string"}},"additionalProperties":false}""");

    public static IReadOnlyDictionary<string, JsonSchemaDocument> All { get; } =
        new[]
        {
            EmptyObject, SearchQuery, ResourceId, NoteBody, AcronymRemember,
            CalendarEventCreate, CalendarEventUpdate, GmailDraftCreate, SheetValuesWrite,
            GitHubIssueCreate, GitHubCommentCreate, GitHubLocalDraft, ObservedItemArray, ExternalIdResult,
        }.ToDictionary(d => d.SchemaId, StringComparer.Ordinal);

    public static ArgumentSchemaRef Ref(JsonSchemaDocument doc) => new(doc.SchemaId, doc.SchemaVersion);

    private static JsonSchemaDocument Doc(string id, string json) => new(id, 1, json);
}
