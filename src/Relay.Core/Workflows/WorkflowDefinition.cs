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
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Args,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("out")] string? Out = null);

/// <summary>Typed workflow input slot.</summary>
public sealed record WorkflowTypedSlot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = true);

/// <summary>
/// Fixture for evaluating a workflow: concrete inputs, optional expected outputs / wait resume values.
/// Promotion requires at least one fixture to execute successfully (not structural-only).
/// </summary>
public sealed record WorkflowFixture(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("inputs")] IReadOnlyDictionary<string, string> Inputs,
    [property: JsonPropertyName("expected")] IReadOnlyDictionary<string, string>? Expected = null,
    [property: JsonPropertyName("resume")] IReadOnlyDictionary<string, string>? Resume = null);

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
        "use_tool", "retrieve", "search", "delegate", "format", "say", "wait", "set",
    ];

    [JsonPropertyName("formatVersion")] public int PackageVersion { get; init; } = FormatVersion;
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    /// <summary>The workflow's own revision number (not the package format version).</summary>
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("inputs")] public IReadOnlyList<WorkflowTypedSlot> Inputs { get; init; } = [];
    [JsonPropertyName("outputs")] public IReadOnlyList<WorkflowTypedSlot> Outputs { get; init; } = [];
    [JsonPropertyName("steps")] public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
    [JsonPropertyName("fixtures")] public IReadOnlyList<WorkflowFixture> Fixtures { get; init; } = [];
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

    /// <summary>Union of permissions implied by step kinds (what promotion advertises).</summary>
    [JsonIgnore] public IReadOnlyList<string> PermissionUnion
    {
        get
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in Steps)
            {
                var kind = (step.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_');
                switch (kind)
                {
                    case "use_tool": set.Add("tool:" + (step.Args.GetValueOrDefault("name") ?? step.Args.GetValueOrDefault("tool") ?? "*")); break;
                    case "retrieve":
                    case "search": set.Add("local_search"); break;
                    case "delegate": set.Add("delegate:" + (step.Args.GetValueOrDefault("profile") ?? "*")); break;
                    case "wait": set.Add("wait"); break;
                    case "format":
                    case "say":
                    case "set": set.Add("local"); break;
                }
            }
            return set.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }
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
                case "wait":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("reason") ?? args.GetValueOrDefault("key")))
                        problems.Add($"step {i + 1} (wait) needs args.reason or args.key");
                    break;
                case "set":
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("name") ?? args.GetValueOrDefault("key")))
                        problems.Add($"step {i + 1} (set) needs args.name");
                    if (string.IsNullOrWhiteSpace(args.GetValueOrDefault("value") ?? args.GetValueOrDefault("from")))
                        problems.Add($"step {i + 1} (set) needs args.value or args.from");
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
            id = s.Id,
            outSlot = s.Out,
            kind = (s.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_'),
            args = (s.Args ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });
        var inputs = Inputs.Select(i => new { i.Name, i.Type, i.Required });
        var outputs = Outputs.Select(o => new { o.Name, o.Type, o.Required });
        var fixtures = Fixtures.Select(f => new
        {
            f.Name,
            inputs = (f.Inputs ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            expected = (f.Expected ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            resume = (f.Resume ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });
        return JsonSerializer.Serialize(new { name = Name, description = Description, version = Version, inputs, outputs, steps, fixtures }, RelayJson.Compact);
    }
}
