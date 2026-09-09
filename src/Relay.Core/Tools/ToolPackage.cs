using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relay.Core.Orchestration;
using Relay.Core.Storage;

namespace Relay.Core.Tools;

/// <summary>One argument a built tool takes; all arguments arrive as strings, like every other tool's.</summary>
public sealed record ToolArgument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required")] bool Required = true);

/// <summary>
/// What a built tool must return for given arguments, checked in the sandbox before promotion. Every
/// test must succeed; <see cref="Keys"/> are keys the result object must have, <see cref="Contains"/> a
/// substring the result's JSON must contain, <see cref="Matches"/> a regular expression it must match.
/// A test with none of the three only requires the call to succeed.
/// </summary>
public sealed record ToolTest(
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Args,
    [property: JsonPropertyName("keys")] IReadOnlyList<string>? Keys = null,
    [property: JsonPropertyName("contains")] string? Contains = null,
    [property: JsonPropertyName("matches")] string? Matches = null);

/// <summary>
/// A tool Relay built for itself (docs/09, slice 6): one JSON file holding the manifest, the JavaScript
/// source and the tests, so a promotion is one change set and a revert removes the whole tool. Drafts
/// live under <c>staging\tools</c>; promoted tools under <c>tools</c>. The source runs only in the worker
/// sandbox and reaches the machine only through the host functions the manifest declares.
/// </summary>
public sealed partial class ToolPackage
{
    public const int Version = 1;
    public const int MaxSourceChars = 20_000;
    public const int MaxDescriptionChars = 200;
    public const int MaxArguments = 8;
    public const int MaxTests = 6;
    public const int MaxNameChars = 40;

    [JsonPropertyName("version")] public int FormatVersion { get; init; } = Version;
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("arguments")] public IReadOnlyList<ToolArgument> Arguments { get; init; } = [];
    /// <summary>The host functions the source may call (<see cref="HostFunctions.Catalog"/>); the broker denies every other one.</summary>
    [JsonPropertyName("hostFunctions")] public IReadOnlyList<string> HostFunctionNames { get; init; } = [];
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("tests")] public IReadOnlyList<ToolTest> Tests { get; init; } = [];
    /// <summary>Who drafted the source (a model name) and for which task; recorded, never trusted.</summary>
    [JsonPropertyName("builtBy")] public string? BuiltBy { get; init; }
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("justification")] public string? Justification { get; init; }
    [JsonPropertyName("draftedAt")] public DateTimeOffset? DraftedAt { get; init; }
    /// <summary>Hash of the source whose tests all passed in the sandbox; promotion requires it to equal the current source's hash.</summary>
    [JsonPropertyName("testedSha256")] public string? TestedSha256 { get; init; }
    [JsonPropertyName("testedAt")] public DateTimeOffset? TestedAt { get; init; }
    [JsonPropertyName("promotedAt")] public DateTimeOffset? PromotedAt { get; init; }

    [JsonIgnore] public string SourceSha256 => Sha(Source);
    [JsonIgnore] public bool Tested => TestedSha256 is not null && string.Equals(TestedSha256, SourceSha256, StringComparison.Ordinal);

    public ToolDescriptor Descriptor => new(Name, Description, Arguments.Select(a => a.Required ? a.Name : a.Name + "?").ToList());

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);

    public static ToolPackage? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ToolPackage>(json, RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A valid tool name: snake_case ASCII, 2–40 characters, starting with a letter.</summary>
    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Everything that must hold before the source is even run: a valid, unused name; a description; bounded
    /// arguments; declared host functions from the catalog only; a bounded source that defines <c>run</c>;
    /// at least one test whose arguments are all declared. Returns the problems, empty when the package is sound.
    /// </summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> reservedNames)
    {
        var problems = new List<string>();
        if (!ValidName(Name)) problems.Add($"name '{Name}' must be snake_case (a-z, 0-9, _), 2–{MaxNameChars} characters, starting with a letter");
        else if (reservedNames.Contains(Name, StringComparer.Ordinal)) problems.Add($"a tool named '{Name}' already exists");
        if (string.IsNullOrWhiteSpace(Description)) problems.Add("description is required");
        else if (Description.Length > MaxDescriptionChars) problems.Add($"description is longer than {MaxDescriptionChars} characters");
        if (Arguments.Count > MaxArguments) problems.Add($"more than {MaxArguments} arguments");
        foreach (var a in Arguments)
        {
            if (!ValidName(a.Name)) problems.Add($"argument '{a.Name}' must be a snake_case identifier");
            if (string.IsNullOrWhiteSpace(a.Description)) problems.Add($"argument '{a.Name}' needs a description");
        }
        if (Arguments.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != Arguments.Count) problems.Add("argument names must be unique");
        foreach (var fn in HostFunctionNames)
            if (!HostFunctions.Catalog.ContainsKey(fn)) problems.Add($"host function '{fn}' does not exist; available: {string.Join(", ", HostFunctions.Catalog.Keys)}");
        if (string.IsNullOrWhiteSpace(Source)) problems.Add("source is required");
        else
        {
            if (Source.Length > MaxSourceChars) problems.Add($"source is longer than {MaxSourceChars} characters");
            if (!RunPattern().IsMatch(Source)) problems.Add("source must define function run(args)");
        }
        if (Tests.Count == 0) problems.Add("at least one test is required");
        if (Tests.Count > MaxTests) problems.Add($"more than {MaxTests} tests");
        var declared = Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < Tests.Count; i++)
        {
            var t = Tests[i];
            foreach (var key in t.Args.Keys) if (!declared.Contains(key)) problems.Add($"test {i + 1} passes '{key}', which is not a declared argument");
            foreach (var a in Arguments) if (a.Required && !t.Args.ContainsKey(a.Name)) problems.Add($"test {i + 1} lacks the required argument '{a.Name}'");
            if (t.Matches is not null)
            {
                try { _ = new Regex(t.Matches, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { problems.Add($"test {i + 1} has an invalid regular expression"); }
            }
        }
        return problems;
    }

    [GeneratedRegex(@"function\s+run\s*\(")]
    private static partial Regex RunPattern();

    /// <summary>Checks one test's expectation against a successful call's result JSON. Null when it holds; otherwise what failed.</summary>
    public static string? Check(ToolTest test, string resultJson)
    {
        if (test.Keys is { Count: > 0 } keys)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(resultJson); }
            catch (JsonException) { return "the result is not valid JSON"; }
            if (node is not JsonObject obj) return "the result is not an object, so it cannot have keys";
            var missing = keys.Where(k => !obj.ContainsKey(k)).ToList();
            if (missing.Count > 0) return $"the result lacks key(s) {string.Join(", ", missing)}; it has {string.Join(", ", obj.Select(kv => kv.Key))}";
        }
        if (!string.IsNullOrEmpty(test.Contains) && !resultJson.Contains(test.Contains, StringComparison.Ordinal))
            return $"the result does not contain \"{test.Contains}\"";
        if (!string.IsNullOrEmpty(test.Matches))
        {
            try { if (!Regex.IsMatch(resultJson, test.Matches, RegexOptions.None, TimeSpan.FromMilliseconds(200))) return $"the result does not match /{test.Matches}/"; }
            catch (RegexMatchTimeoutException) { return "the regular expression timed out"; }
        }
        return null;
    }
}
