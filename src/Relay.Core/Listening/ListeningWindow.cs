using System.Text.Json.Serialization;

namespace Relay.Core.Listening;

/// <summary>Durable listening window awaiting or holding semantic processing results.</summary>
public sealed class ListeningWindow
{
    [JsonPropertyName("windowId")] public required string WindowId { get; init; }
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; set; }
    [JsonPropertyName("primary")] public ListeningCoverage Primary { get; set; } = new() { Role = CoverageRoles.Primary };
    [JsonPropertyName("context")] public ListeningCoverage Context { get; set; } = new() { Role = CoverageRoles.Context };
    [JsonPropertyName("decisionDefinitionVersions")] public Dictionary<string, string> DecisionDefinitionVersions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("status")] public string Status { get; set; } = ListeningWindowStatus.Pending;
    [JsonPropertyName("findingIds")] public List<string> FindingIds { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];

    public IEnumerable<string> AllCoveredSegmentIds()
        => Primary.SegmentIds.Concat(Context.SegmentIds).Distinct(StringComparer.Ordinal);
}
