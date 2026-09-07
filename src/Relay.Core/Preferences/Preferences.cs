using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.SelfChange;
using Relay.Core.Storage;

namespace Relay.Core.Preferences;

/// <summary>
/// The user's typed preferences. Prose never reaches a prompt unread: each section is compiled into
/// the exact prompt fragment, generation limit, display policy, or standing grant it stands for.
/// The file changes only through change sets, so every preference edit has a before image.
/// </summary>
public sealed class UserPreferences
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("response")] public ResponsePreferences Response { get; set; } = new();
    [JsonPropertyName("display")] public DisplayPreferences Display { get; set; } = new();
    [JsonPropertyName("filing")] public FilingPreferences Filing { get; set; } = new();
    [JsonPropertyName("retention")] public RetentionPreferences Retention { get; set; } = new();
    [JsonPropertyName("sources")] public SourcePreferences Sources { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!ResponsePreferences.Verbosities.Contains(Response.Verbosity)) problems.Add($"response.verbosity must be one of {string.Join(", ", ResponsePreferences.Verbosities)}.");
        if (Display.MaxAlertsPer10Minutes is < 0 or > 50) problems.Add("display.maxAlertsPer10Minutes must be 0–50.");
        if (Display.CooldownSeconds is < 0 or > 3600) problems.Add("display.cooldownSeconds must be 0–3600.");
        if (Retention.BufferSeconds is < 15 or > 600) problems.Add("retention.bufferSeconds must be 15–600.");
        if (Retention.ExcerptMaxSeconds is < 5 or > 120) problems.Add("retention.excerptMaxSeconds must be 5–120.");
        if (Retention.MaxRetainedFraction is <= 0 or > 1) problems.Add("retention.maxRetainedFraction must be in (0, 1].");
        foreach (var g in Filing.Grants)
        {
            if (string.IsNullOrWhiteSpace(g.Action)) problems.Add("A filing grant has no action.");
            if (g.Action is Policy.Actions.DeleteProject or Policy.Actions.ModelRequest or Policy.Actions.UpdatePreference or Policy.Actions.UpdatePrompt)
                problems.Add($"A standing grant may not cover '{g.Action}'; it always needs a fresh approval.");
        }
        return problems;
    }

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);
}

public sealed class ResponsePreferences
{
    public const string Minimalist = "minimalist";
    public const string Concise = "concise";
    public const string Normal = "normal";
    public static readonly string[] Verbosities = [Minimalist, Concise, Normal];

    [JsonPropertyName("verbosity")] public string Verbosity { get; set; } = Concise;
    /// <summary>Extra prompt lines the user approved through change sets (e.g. "never use bullet lists").</summary>
    [JsonPropertyName("promptLines")] public List<string> PromptLines { get; set; } = new();
}

public sealed class DisplayPreferences
{
    /// <summary>Terms whose definition stays pinned and is refreshed in place whenever they are mentioned.</summary>
    [JsonPropertyName("alwaysShowTerms")] public List<string> AlwaysShowTerms { get; set; } = new();
    [JsonPropertyName("maxAlertsPer10Minutes")] public int MaxAlertsPer10Minutes { get; set; } = 3;
    /// <summary>A finding with the same merge key inside the cool-down refreshes the earlier card instead of adding one.</summary>
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; } = 300;
    [JsonPropertyName("maxResultsPer5Minutes")] public int MaxResultsPer5Minutes { get; set; } = 6;
}

/// <summary>A standing approval: this action, for this project (or any), automatically. Always granted by the user; always revocable.</summary>
public sealed class StandingGrant
{
    [JsonPropertyName("grantId")] public string GrantId { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
    [JsonPropertyName("noteType")] public string? NoteType { get; set; }
    [JsonPropertyName("grantedAt")] public DateTimeOffset GrantedAt { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";

    public bool Covers(string action, IReadOnlyDictionary<string, string> target)
    {
        if (action != Action) return false;
        if (ProjectId is not null && target.GetValueOrDefault("projectId") != ProjectId) return false;
        if (NoteType is not null && target.GetValueOrDefault("type") != NoteType) return false;
        return true;
    }
}

public sealed class FilingPreferences
{
    [JsonPropertyName("grants")] public List<StandingGrant> Grants { get; set; } = new();
}

public sealed class RetentionPreferences
{
    [JsonPropertyName("bufferSeconds")] public int BufferSeconds { get; set; } = 90;
    [JsonPropertyName("excerptMaxSeconds")] public int ExcerptMaxSeconds { get; set; } = 30;
    [JsonPropertyName("maxRetainedFraction")] public double MaxRetainedFraction { get; set; } = 0.25;
}

public sealed class SourcePreferences
{
    /// <summary>Whether observed tasks may propose online search at all. Direct asks may still propose it per task.</summary>
    [JsonPropertyName("allowOnlineSearch")] public bool AllowOnlineSearch { get; set; }
    [JsonPropertyName("connectedAccounts")] public List<string> ConnectedAccounts { get; set; } = new();
}

/// <summary>What the preferences compile to. Consumers take these, never the preference prose.</summary>
public sealed record CompiledPreferences(
    string PromptFragment,
    int MaxAnswerTokens,
    int MaxAnswerChars,
    IReadOnlyList<string> WatchedTerms,
    IReadOnlyList<StandingGrant> Grants,
    int MaxAlertsPer10Minutes,
    int MaxResultsPer5Minutes,
    TimeSpan Cooldown,
    TimeSpan Buffer,
    double ExcerptMaxSeconds,
    double MaxRetainedFraction,
    bool AllowOnlineSearch,
    /// <summary>The response style the limits were compiled from (minimalist, concise, normal); shown in the UI, never read by consumers.</summary>
    string Verbosity = ResponsePreferences.Concise);

public static class PreferenceCompiler
{
    public static CompiledPreferences Compile(UserPreferences p)
    {
        var (tokens, chars, style) = p.Response.Verbosity switch
        {
            ResponsePreferences.Minimalist => (160, 240, "Answer in one short sentence or a single line. No preamble, no restating the question, no closing remarks."),
            ResponsePreferences.Concise => (400, 700, "Answer in at most three short sentences. No preamble, no filler, no restating the question. Facts and sources only."),
            _ => (900, 2000, "Answer plainly and completely, without filler."),
        };
        var lines = new List<string> { style };
        lines.AddRange(p.Response.PromptLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()));
        return new CompiledPreferences(
            string.Join(" ", lines),
            tokens,
            chars,
            p.Display.AlwaysShowTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            p.Filing.Grants.ToList(),
            p.Display.MaxAlertsPer10Minutes,
            p.Display.MaxResultsPer5Minutes,
            TimeSpan.FromSeconds(p.Display.CooldownSeconds),
            TimeSpan.FromSeconds(p.Retention.BufferSeconds),
            p.Retention.ExcerptMaxSeconds,
            p.Retention.MaxRetainedFraction,
            p.Sources.AllowOnlineSearch,
            p.Response.Verbosity);
    }
}

/// <summary>Reads preferences; writes only through the change-set store so every edit is reversible.</summary>
public sealed class PreferenceStore
{
    private readonly DataRoot _root;
    private readonly ChangeSetStore _changes;

    public PreferenceStore(DataRoot root, ChangeSetStore changes)
    {
        _root = root;
        _changes = changes;
    }

    public UserPreferences Load(ICollection<string>? problems = null)
    {
        var text = AtomicFile.ReadAllTextIfExists(_root.PreferencesPath);
        if (text is null) return new UserPreferences();
        try
        {
            var prefs = JsonSerializer.Deserialize<UserPreferences>(text, RelayJson.Indented) ?? new UserPreferences();
            var invalid = prefs.Validate();
            if (invalid.Count == 0) return prefs;
            foreach (var p in invalid) problems?.Add(p);
            return new UserPreferences();
        }
        catch (JsonException ex)
        {
            problems?.Add("preferences.json is not valid JSON: " + ex.Message);
            return new UserPreferences();
        }
    }

    /// <summary>Applies a mutation as one change set. Returns the change set, or the reason nothing was written.</summary>
    public ChangeSetResult Update(Action<UserPreferences> mutate, string reason, DateTimeOffset now, string? taskId = null, string? proposalId = null)
    {
        var prefs = Load();
        mutate(prefs);
        var problems = prefs.Validate();
        if (problems.Count > 0) return new ChangeSetResult(false, null, string.Join(" ", problems));
        return _changes.Apply(ChangeKinds.Preference, _root.PreferencesPath, prefs.ToJson(), reason, now, taskId, proposalId);
    }

    public CompiledPreferences Compiled() => PreferenceCompiler.Compile(Load());

    /// <summary>The current value of one preference by its self-change key; lists join with "; ". Null for unknown keys.</summary>
    public string? Get(string key)
    {
        var p = Load();
        return key switch
        {
            "response.verbosity" => p.Response.Verbosity,
            "response.promptLine" => string.Join("; ", p.Response.PromptLines),
            "display.alwaysShow" => string.Join("; ", p.Display.AlwaysShowTerms),
            "display.maxAlertsPer10Minutes" => p.Display.MaxAlertsPer10Minutes.ToString(),
            "display.maxResultsPer5Minutes" => p.Display.MaxResultsPer5Minutes.ToString(),
            "display.cooldownSeconds" => p.Display.CooldownSeconds.ToString(),
            "filing.grant" => string.Join("; ", p.Filing.Grants.Select(g => $"{g.Action}:{g.ProjectId}{(g.NoteType is null ? "" : "/" + g.NoteType)}")),
            "retention.bufferSeconds" => p.Retention.BufferSeconds.ToString(),
            "retention.excerptMaxSeconds" => p.Retention.ExcerptMaxSeconds.ToString(),
            "retention.maxRetainedFraction" => p.Retention.MaxRetainedFraction.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "sources.allowOnlineSearch" => p.Sources.AllowOnlineSearch ? "true" : "false",
            _ => null,
        };
    }
}
