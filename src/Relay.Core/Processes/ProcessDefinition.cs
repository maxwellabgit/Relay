using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Processes;

/// <summary>Finite node types for Jev production process graphs (§10).</summary>
public static class ProcessNodeTypes
{
    public const string Retrieve = "retrieve";
    public const string Judge = "judge";
    public const string LocalJob = "local_job";
    public const string ReasoningJob = "reasoning_job";
    public const string Tool = "tool";
    public const string Approval = "approval";
    public const string Branch = "branch";
    public const string Present = "present";
    public const string Wait = "wait";
    public const string Complete = "complete";

    public static readonly string[] All =
    [
        Retrieve, Judge, LocalJob, ReasoningJob, Tool, Approval, Branch, Present, Wait, Complete,
    ];
}

public sealed class ProcessDefinition
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "1";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("entry")] public required string Entry { get; init; }
    [JsonPropertyName("nodes")] public List<ProcessNode> Nodes { get; init; } = [];
    [JsonPropertyName("edges")] public List<ProcessEdge> Edges { get; init; } = [];
}

public sealed class ProcessNode
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("decisionId")] public string? DecisionId { get; init; }
    [JsonPropertyName("jobType")] public string? JobType { get; init; }
    [JsonPropertyName("tool")] public string? Tool { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("args")] public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
}

public sealed class ProcessEdge
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
    [JsonPropertyName("retry")] public ProcessRetryBound? Retry { get; init; }
}

public sealed class ProcessRetryBound
{
    [JsonPropertyName("maxAttempts")] public int MaxAttempts { get; init; } = 1;
    [JsonPropertyName("backoffMs")] public int BackoffMs { get; init; } = 0;
}

public sealed class ProcessRunState
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("workflowId")] public required string WorkflowId { get; init; }
    [JsonPropertyName("workflowVersion")] public string WorkflowVersion { get; init; } = "1";
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("currentNodeId")] public string? CurrentNodeId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("nodeOutputs")] public Dictionary<string, JsonElement> NodeOutputs { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("retryCounts")] public Dictionary<string, int> RetryCounts { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
}

public static class ProcessCatalog
{
    public static ProcessDefinition Load(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProcessDefinition>(text, RelayJson.Indented)
               ?? throw new InvalidOperationException("Invalid process: " + path);
    }

    public static IReadOnlyList<ProcessDefinition> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.json")
            .Select(Load)
            .OrderBy(w => w.Id, StringComparer.Ordinal)
            .ToList();
    }
}
