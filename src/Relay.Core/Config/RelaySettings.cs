using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Input;
using Relay.Core.Storage;

namespace Relay.Core.Config;

/// <summary>
/// User-editable settings. Stored as <c>config\settings.json</c> under the data root.
/// Defaults follow the interaction contract: two window-scoped chords for the primary toggles,
/// the rule-based orchestrator on, the model gateway off.
/// </summary>
public sealed class RelaySettings
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 2;
    [JsonPropertyName("hotkeys")] public HotkeySettings Hotkeys { get; set; } = new();
    [JsonPropertyName("capture")] public CaptureSettings Capture { get; set; } = new();
    [JsonPropertyName("stream")] public StreamSettings Stream { get; set; } = new();
    [JsonPropertyName("judge")] public JudgeSettings Judge { get; set; } = new();
    [JsonPropertyName("diagnostics")] public DiagnosticsSettings Diagnostics { get; set; } = new();
    [JsonPropertyName("orchestrator")] public OrchestratorSettings Orchestrator { get; set; } = new();
    [JsonPropertyName("model")] public ModelSettings Model { get; set; } = new();
    [JsonPropertyName("externalModels")] public List<ExternalModelProfile> ExternalModels { get; set; } = new();
    [JsonPropertyName("workers")] public WorkerSettings Workers { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Orchestrator.Mode is not (OrchestratorSettings.Off or OrchestratorSettings.Rules or OrchestratorSettings.RulesAndModel or OrchestratorSettings.Mind))
            problems.Add($"orchestrator.mode must be one of off, rules, rules+model, mind (was '{Orchestrator.Mode}').");
        if (Orchestrator.MaxSteps is < 2 or > 100) problems.Add("orchestrator.maxSteps must be 2–100.");
        if (Orchestrator.AutoRouteThreshold is < 0 or > 1) problems.Add("orchestrator.autoRouteThreshold must be between 0 and 1.");
        if (Orchestrator.ReviewThreshold is < 0 or > 1 || Orchestrator.ReviewThreshold > Orchestrator.AutoRouteThreshold)
            problems.Add("orchestrator.reviewThreshold must be between 0 and autoRouteThreshold.");
        if (Orchestrator.PlanningTimeoutMs < 1000) problems.Add("orchestrator.planningTimeoutMs must be at least 1000.");
        if (Model.Enabled)
        {
            if (!ModelSettings.IsAllowedEndpoint(Model.Endpoint, out var why)) problems.Add("model.endpoint: " + why);
            if (string.IsNullOrWhiteSpace(Model.Model)) problems.Add("model.model must name a model.");
            if (Model.TimeoutMs < 1000) problems.Add("model.timeoutMs must be at least 1000.");
        }
        foreach (var profile in ExternalModels)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) problems.Add("externalModels[].name must be a simple identifier.");
            if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) problems.Add($"externalModels[{profile.Name}].endpoint must be an absolute https URL; external models never use plain http.");
            if (string.IsNullOrWhiteSpace(profile.Model)) problems.Add($"externalModels[{profile.Name}].model must name a model.");
            if (string.IsNullOrWhiteSpace(profile.SecretName)) problems.Add($"externalModels[{profile.Name}].secretName is required.");
        }
        if (ExternalModels.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != ExternalModels.Count) problems.Add("externalModels names must be unique.");
        if (Judge.Mode is not (JudgeSettings.Off or JudgeSettings.Heuristic or JudgeSettings.Model)) problems.Add("judge.mode must be off, heuristic, or model.");
        if (Judge.MinConfidence is < 0 or > 1) problems.Add("judge.minConfidence must be between 0 and 1.");
        if (Judge.TimeoutMs < 1000) problems.Add("judge.timeoutMs must be at least 1000.");
        if (Stream.BufferSeconds != StreamSettings.WholeConversation && Stream.BufferSeconds is < 15 or > 600) problems.Add("stream.bufferSeconds must be 0 (whole conversation) or 15–600.");
        if (Stream.SegmentQuietMs is < 200 or > 10000) problems.Add("stream.segmentQuietMs must be 200–10000.");
        if (Stream.ObserveIntervalMs is < 500 or > 60000) problems.Add("stream.observeIntervalMs must be 500–60000.");
        if (Stream.MinIngestChars is < 0 or > 5000) problems.Add("stream.minIngestChars must be 0–5000.");
        if (Stream.MinIngestSeconds is < 0 or > 300) problems.Add("stream.minIngestSeconds must be 0–300.");
        if (Stream.ExcerptMaxSeconds is < 5 or > 120) problems.Add("stream.excerptMaxSeconds must be 5–120.");
        if (Stream.MaxRetainedFraction is <= 0 or > 1) problems.Add("stream.maxRetainedFraction must be in (0, 1].");
        if (Workers.WallClockSeconds < 5) problems.Add("workers.wallClockSeconds must be at least 5.");
        if (Workers.MemoryMb < 64) problems.Add("workers.memoryMb must be at least 64.");
        if (Workers.MaxToolCalls < 10) problems.Add("workers.maxToolCalls must be at least 10.");
        if (Workers.MaxReadBytes < 65536 || Workers.MaxWriteBytes < 4096) problems.Add("workers.maxReadBytes/maxWriteBytes are too small to do anything.");
        if (Hotkeys.Scope is not (HotkeySettings.WindowScope or HotkeySettings.GlobalScope)) problems.Add("hotkeys.scope must be \"window\" or \"global\".");
        var modifierOnlyOk = Hotkeys.IsWindowScoped;
        if (!KeyChord.TryParse(Hotkeys.NoteKey, modifierOnlyOk, out var note, out var e1)) problems.Add($"hotkeys.noteKey: {e1}" + (modifierOnlyOk ? "" : " (a global hotkey needs a key, e.g. Ctrl+Alt+N)"));
        if (!KeyChord.TryParse(Hotkeys.CommandKey, modifierOnlyOk, out var command, out var e2)) problems.Add($"hotkeys.commandKey: {e2}" + (modifierOnlyOk ? "" : " (a global hotkey needs a key, e.g. Ctrl+Alt+N)"));
        if (note is not null && command is not null && note == command) problems.Add("hotkeys.noteKey and hotkeys.commandKey must differ.");
        if (Capture.TranscriptTimeoutMs < 1000) problems.Add("capture.transcriptTimeoutMs must be at least 1000.");
        if (Capture.StabilizationMs < 100) problems.Add("capture.stabilizationMs must be at least 100.");
        return problems;
    }

    public string ComputeHash()
    {
        var json = JsonSerializer.Serialize(this, RelayJson.Compact);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

public sealed class HotkeySettings
{
    /// <summary>Chords work only while the Relay window is active; they never reach other applications.</summary>
    public const string WindowScope = "window";
    /// <summary>Chords are registered system-wide with RegisterHotKey and need a non-modifier key.</summary>
    public const string GlobalScope = "global";

    [JsonPropertyName("scope")] public string Scope { get; set; } = WindowScope;
    /// <summary>NOTE_KEY: starts or stops silent note capture.</summary>
    [JsonPropertyName("noteKey")] public string NoteKey { get; set; } = "Ctrl+Alt";
    /// <summary>COMMAND_KEY: opens or closes an instruction turn.</summary>
    [JsonPropertyName("commandKey")] public string CommandKey { get; set; } = "Ctrl+X";

    public bool IsWindowScoped => Scope == WindowScope;
}

public sealed class CaptureSettings
{
    /// <summary>How long AWAITING_TRANSCRIPT waits for the first text before reporting a timeout.</summary>
    [JsonPropertyName("transcriptTimeoutMs")] public int TranscriptTimeoutMs { get; set; } = 10_000;
    /// <summary>Quiet period after the last text change before the transcript is considered complete. Flow inserts its transcript in one burst, so a short window is enough.</summary>
    [JsonPropertyName("stabilizationMs")] public int StabilizationMs { get; set; } = 600;
    /// <summary>How long the COMPLETED receipt stays before returning to IDLE automatically.</summary>
    [JsonPropertyName("completedReceiptMs")] public int CompletedReceiptMs { get; set; } = 4_000;
    /// <summary>Debounce for rewriting the crash-safe draft while text arrives.</summary>
    [JsonPropertyName("draftPersistDebounceMs")] public int DraftPersistDebounceMs { get; set; } = 200;
}

public sealed class DiagnosticsSettings
{
    /// <summary>Process names (without .exe) that identify a running Wispr Flow instance.</summary>
    [JsonPropertyName("flowProcessNames")] public List<string> FlowProcessNames { get; set; } = ["Wispr Flow", "WisprFlow", "Flow"];
}

public sealed class OrchestratorSettings
{
    public const string Off = "off";
    public const string Rules = "rules";
    public const string RulesAndModel = "rules+model";
    /// <summary>The rebuilt orchestrator: one mind, one self-observing loop per task (docs/09). Needs the model enabled.</summary>
    public const string Mind = "mind";

    /// <summary>off: instructions are recorded only. rules: deterministic command grammar only. rules+model: RELAY0's model plans first, the grammar is its fallback. mind: the loop of docs/09 runs every task.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = RulesAndModel;
    /// <summary>Mind mode: the most steps one task may take before it is ended visibly.</summary>
    [JsonPropertyName("maxSteps")] public int MaxSteps { get; set; } = 12;
    /// <summary>Notes routed at or above this confidence are filed into the project automatically.</summary>
    [JsonPropertyName("autoRouteThreshold")] public double AutoRouteThreshold { get; set; } = 0.75;
    /// <summary>Notes between this and the auto threshold go to Review; below it they stay unrouted in staging.</summary>
    [JsonPropertyName("reviewThreshold")] public double ReviewThreshold { get; set; } = 0.35;
    [JsonPropertyName("planningTimeoutMs")] public int PlanningTimeoutMs { get; set; } = 60_000;
    [JsonPropertyName("maxToolCalls")] public int MaxToolCalls { get; set; } = 8;
}

/// <summary>RELAY0's model: the local judge and planner. Loopback http (llama.cpp on this machine) or https.</summary>
public sealed class ModelSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>The endpoint RELAY0 talks to. OpenAI-compatible chat completions; plain http only on loopback.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "http://127.0.0.1:8080/v1/chat/completions";
    [JsonPropertyName("model")] public string Model { get; set; } = "ministral-8b-instruct";
    /// <summary>Name of the DPAPI-protected secret holding the API key. Never stored in this file. Optional for loopback.</summary>
    [JsonPropertyName("secretName")] public string SecretName { get; set; } = "model-gateway";
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 30_000;
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; } = 800;

    public bool IsLoopback => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>https anywhere, or http strictly on loopback (127.0.0.1, ::1, localhost). Nothing else.</summary>
    public static bool IsAllowedEndpoint(string? endpoint, out string reason)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) { reason = "must be an absolute URL."; return false; }
        if (uri.Scheme == Uri.UriSchemeHttps) { reason = ""; return true; }
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) { reason = ""; return true; }
        reason = "must be https, or http on loopback (127.0.0.1 / localhost) for a local model server.";
        return false;
    }
}

/// <summary>A named external model the planner may propose delegating to. Always https; every request is a separate approval.</summary>
public sealed class ExternalModelProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "research";
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-5-nano";
    [JsonPropertyName("secretName")] public string SecretName { get; set; } = "external-research";
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 120_000;
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; } = 4_000;
    /// <summary>Whether the profile's host may be asked to search online on Relay's behalf (only when a task is approved with allowSearch).</summary>
    [JsonPropertyName("supportsSearch")] public bool SupportsSearch { get; set; }

    public ModelSettings AsModelSettings() => new() { Enabled = true, Endpoint = Endpoint, Model = Model, SecretName = SecretName, TimeoutMs = TimeoutMs, MaxOutputTokens = MaxOutputTokens };
}

/// <summary>Listening: how the stream is cut, how long it is held, how often RELAY0 judges it, how much may be retained.</summary>
public sealed class StreamSettings
{
    /// <summary>Whole conversation: nothing heard is dropped until the stream stops.</summary>
    public const int WholeConversation = 0;

    /// <summary>
    /// Seconds of talk held while listening. 0 holds the whole conversation for the session (the default while the
    /// architecture is being built, until it is clear what can be dropped early); 15–600 is a rolling window.
    /// </summary>
    [JsonPropertyName("bufferSeconds")] public int BufferSeconds { get; set; } = WholeConversation;
    /// <summary>Text without a sentence end becomes a segment after this much quiet.</summary>
    [JsonPropertyName("segmentQuietMs")] public int SegmentQuietMs { get; set; } = 1_200;
    /// <summary>How often the judge is offered new segments (watched terms and acronyms are judged at once).</summary>
    [JsonPropertyName("observeIntervalMs")] public int ObserveIntervalMs { get; set; } = 12_000;
    /// <summary>A pass over new talk waits until at least this many new characters have arrived…</summary>
    [JsonPropertyName("minIngestChars")] public int MinIngestChars { get; set; } = 240;
    /// <summary>…or until the oldest unjudged segment is this old, so a lone sentence is still judged after a pause. Watched terms and stopping the stream never wait. 0 for both judges every fragment.</summary>
    [JsonPropertyName("minIngestSeconds")] public int MinIngestSeconds { get; set; } = 20;
    [JsonPropertyName("excerptMaxSeconds")] public int ExcerptMaxSeconds { get; set; } = 30;
    [JsonPropertyName("maxRetainedFraction")] public double MaxRetainedFraction { get; set; } = 0.25;
    /// <summary>Mind mode: the most moves one listening pass may take. Almost every pass takes one (wait).</summary>
    [JsonPropertyName("maxMovesPerPass")] public int MaxMovesPerPass { get; set; } = 3;
    /// <summary>Mind mode: read-only checks the mind may make in one pass before raising work or waiting.</summary>
    [JsonPropertyName("maxToolCallsPerPass")] public int MaxToolCallsPerPass { get; set; } = 2;
    /// <summary>Mind mode: pieces of work one pass may raise, so one stretch of talk cannot fill the queue.</summary>
    [JsonPropertyName("maxRaisesPerPass")] public int MaxRaisesPerPass { get; set; } = 2;
}

public sealed class JudgeSettings
{
    public const string Off = "off";
    public const string Heuristic = "heuristic";
    public const string Model = "model";

    /// <summary>off: streams are buffered and discarded, nothing is judged. heuristic: cue words (labeled as such). model: RELAY0 judges; falls back to heuristic when the model is unavailable.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = Model;
    [JsonPropertyName("minConfidence")] public double MinConfidence { get; set; } = 0.55;
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 8_000;
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; } = 600;
}

public sealed class WorkerSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("wallClockSeconds")] public int WallClockSeconds { get; set; } = 120;
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 512;
    [JsonPropertyName("maxToolCalls")] public int MaxToolCalls { get; set; } = 400;
    [JsonPropertyName("maxReadBytes")] public long MaxReadBytes { get; set; } = 8L * 1024 * 1024;
    [JsonPropertyName("maxWriteBytes")] public long MaxWriteBytes { get; set; } = 2L * 1024 * 1024;
    /// <summary>Path to Relay.Worker.exe; null means the copy beside Relay.exe.</summary>
    [JsonPropertyName("executable")] public string? Executable { get; set; }
}

public static class SettingsStore
{
    public sealed record LoadResult(RelaySettings Settings, bool CreatedDefault, IReadOnlyList<string> Problems, string Hash);

    /// <summary>
    /// Loads settings, creating defaults when missing. Invalid settings are reported, and the
    /// offending sections fall back to defaults so the application can still start and show
    /// the problem instead of failing silently.
    /// </summary>
    public static LoadResult Load(DataRoot root)
    {
        var text = AtomicFile.ReadAllTextIfExists(root.SettingsPath);
        if (text is null)
        {
            var defaults = new RelaySettings();
            AtomicFile.WriteAllText(root.SettingsPath, JsonSerializer.Serialize(defaults, RelayJson.Indented));
            return new LoadResult(defaults, true, [], defaults.ComputeHash());
        }

        RelaySettings settings;
        var problems = new List<string>();
        try
        {
            settings = JsonSerializer.Deserialize<RelaySettings>(text, RelayJson.Indented) ?? new RelaySettings();
        }
        catch (JsonException ex)
        {
            problems.Add($"settings.json could not be parsed ({ex.Message}); defaults are in effect.");
            settings = new RelaySettings();
        }

        var validation = settings.Validate();
        if (validation.Count > 0)
        {
            problems.AddRange(validation);
            // Fall back per section so one bad value does not disable everything.
            var defaults = new RelaySettings();
            if (validation.Any(p => p.StartsWith("hotkeys", StringComparison.Ordinal))) settings.Hotkeys = defaults.Hotkeys;
            if (validation.Any(p => p.StartsWith("capture", StringComparison.Ordinal))) settings.Capture = defaults.Capture;
            if (validation.Any(p => p.StartsWith("orchestrator", StringComparison.Ordinal))) settings.Orchestrator = defaults.Orchestrator;
            if (validation.Any(p => p.StartsWith("model", StringComparison.Ordinal))) settings.Model = defaults.Model;
            if (validation.Any(p => p.StartsWith("externalModels", StringComparison.Ordinal))) settings.ExternalModels = defaults.ExternalModels;
            if (validation.Any(p => p.StartsWith("judge", StringComparison.Ordinal))) settings.Judge = defaults.Judge;
            if (validation.Any(p => p.StartsWith("stream", StringComparison.Ordinal))) settings.Stream = defaults.Stream;
            if (validation.Any(p => p.StartsWith("workers", StringComparison.Ordinal))) settings.Workers = defaults.Workers;
        }
        return new LoadResult(settings, false, problems, settings.ComputeHash());
    }

    /// <summary>Writes settings atomically after validation. Returns the problems when invalid (nothing is written).</summary>
    public static IReadOnlyList<string> Save(DataRoot root, RelaySettings settings)
    {
        var problems = settings.Validate();
        if (problems.Count > 0) return problems;
        AtomicFile.WriteAllText(root.SettingsPath, JsonSerializer.Serialize(settings, RelayJson.Indented));
        return problems;
    }
}
