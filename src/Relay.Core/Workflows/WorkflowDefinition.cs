using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relay.Core.Storage;

namespace Relay.Core.Workflows;

/// <summary>One step of a workflow: a kind the loop already understands, plus a flat string map of arguments.</summary>
public sealed record WorkflowStep(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Args);

/// <summary>What the mind is told about a promoted workflow: name, description, version, and the step kinds in order.</summary>
public sealed record WorkflowDescriptor(string Name, string Description, int Version, IReadOnlyList<string> StepKinds);

/// <summary>
/// A named, versioned workflow definition (Alpha Step 4): a list of steps that compose context retrieval,
/// tool calls, delegation and formatting. Drafts live under <c>staging\workflows</c>; promoted workflows
/// under <c>workflows</c>, each written by one change set so a revert removes the whole definition.
/// </summary>
public sealed partial class WorkflowDefinition
{
    public const int FormatVersion = 1;
    public const int MaxDescriptionChars = 200;
    public const int MaxSteps = 12;
    public const int MaxNameChars = 40;

    public static readonly string[] StepKinds =
    [
        "use_tool", "retrieve", "search", "delegate", "format", "say",
    ];

    [JsonPropertyName("formatVersion")] public int PackageVersion { get; init; } = FormatVersion;
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    /// <summary>The workflow's own revision number (not the package format version).</summary>
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("steps")] public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
    [JsonPropertyName("builtBy")] public string? BuiltBy { get; init; }
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("justification")] public string? Justification { get; init; }
    [JsonPropertyName("draftedAt")] public DateTimeOffset? DraftedAt { get; init; }
    /// <summary>Hash of the definition whose dry-run passed; promotion requires it to equal the current definition hash.</summary>
    [JsonPropertyName("testedSha256")] public string? TestedSha256 { get; init; }
    [JsonPropertyName("testedAt")] public DateTimeOffset? TestedAt { get; init; }
    [JsonPropertyName("promotedAt")] public DateTimeOffset? PromotedAt { get; init; }

    /// <summary>SHA-256 of the durable definition (name, description, version, steps) — what approval pins.</summary>
    [JsonIgnore] public string DefinitionSha256 => Sha(Canonical());

    [JsonIgnore] public bool Tested => TestedSha256 is not null && string.Equals(TestedSha256, DefinitionSha256, StringComparison.Ordinal);

    [JsonIgnore] public WorkflowDescriptor Descriptor => new(Name, Description, Version, Steps.Select(s => s.Kind).ToList());

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);

    public static WorkflowDefinition? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowDefinition>(json, RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A valid workflow name: snake_case ASCII, 2–40 characters, starting with a letter.</summary>
    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Everything that must hold before a dry-run: a valid unused name, a description, and at least one
    /// well-formed step. Returns the problems, empty when the definition is sound.
    /// </summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> reservedNames)
    {
        var problems = new List<string>();
        if (!ValidName(Name)) problems.Add($"name '{Name}' must be snake_case (a-z, 0-9, _; 2–{MaxNameChars} characters, starting with a letter)");
        else if (reservedNames.Contains(Name, StringComparer.Ordinal)) problems.Add($"a workflow named '{Name}' already exists");
        if (string.IsNullOrWhiteSpace(Description)) problems.Add("description is required");
        else if (Description.Length > MaxDescriptionChars) problems.Add($"description is longer than {MaxDescriptionChars} characters");
        if (Version < 1) problems.Add("version must be at least 1");
        if (Steps.Count == 0) problems.Add("at least one step is required");
        if (Steps.Count > MaxSteps) problems.Add($"more than {MaxSteps} steps");
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var kind = (step.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_');
            if (!StepKinds.Contains(kind, StringComparer.Ordinal))
            {
                problems.Add($"step {i + 1}: unknown kind '{step.Kind}'; allowed: {string.Join(", ", StepKinds)}");
                continue;
            }
            var args = step.Args ?? new Dictionary<string, string>();
            switch (kind)
            {
                case "use_tool":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("name") ?? args.GetValueOrDefault("tool")))
                        problems.Add($"step {i + 1} (use_tool) needs args.name (the tool)");
                    break;
                case "retrieve":
                case "search":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("query")))
                        problems.Add($"step {i + 1} ({kind}) needs args.query");
                    break;
                case "delegate":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("profile")))
                        problems.Add($"step {i + 1} (delegate) needs args.profile");
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("prompt") ?? args.GetValueOrDefault("objective") ?? args.GetValueOrDefault("text")))
                        problems.Add($"step {i + 1} (delegate) needs args.prompt (or objective)");
                    break;
                case "format":
                case "say":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("text")))
                        problems.Add($"step {i + 1} ({kind}) needs args.text");
                    break;
            }
        }
        return problems;
    }

    /// <summary>Stable payload hashed for <see cref="DefinitionSha256"/> — excludes draft/test/promote metadata.</summary>
    private string Canonical()
    {
        var steps = Steps.Select(s => new
        {
            kind = (s.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_'),
            args = (s.Args ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });
        return JsonSerializer.Serialize(new { name = Name, description = Description, version = Version, steps }, RelayJson.Compact);
    }
}
