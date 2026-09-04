using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Input;
using Relay.Core.Storage;

namespace Relay.Core.Config;

/// <summary>
/// User-editable settings. Stored as <c>config\settings.json</c> under the data root.
/// Defaults follow the v0.1 contract: F13 / F14 for the two primary toggles and the Flow
/// relay disabled until the user has reserved a private Flow hands-free chord.
/// </summary>
public sealed class RelaySettings
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("hotkeys")] public HotkeySettings Hotkeys { get; set; } = new();
    [JsonPropertyName("flowRelay")] public FlowRelaySettings FlowRelay { get; set; } = new();
    [JsonPropertyName("capture")] public CaptureSettings Capture { get; set; } = new();
    [JsonPropertyName("diagnostics")] public DiagnosticsSettings Diagnostics { get; set; } = new();
    [JsonPropertyName("orchestrator")] public OrchestratorSettings Orchestrator { get; set; } = new();
    [JsonPropertyName("model")] public ModelSettings Model { get; set; } = new();
    [JsonPropertyName("workers")] public WorkerSettings Workers { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Orchestrator.Mode is not (OrchestratorSettings.Off or OrchestratorSettings.Rules or OrchestratorSettings.RulesAndModel))
            problems.Add($"orchestrator.mode must be one of off, rules, rules+model (was '{Orchestrator.Mode}').");
        if (Orchestrator.AutoRouteThreshold is < 0 or > 1) problems.Add("orchestrator.autoRouteThreshold must be between 0 and 1.");
        if (Orchestrator.ReviewThreshold is < 0 or > 1 || Orchestrator.ReviewThreshold > Orchestrator.AutoRouteThreshold)
            problems.Add("orchestrator.reviewThreshold must be between 0 and autoRouteThreshold.");
        if (Orchestrator.PlanningTimeoutMs < 1000) problems.Add("orchestrator.planningTimeoutMs must be at least 1000.");
        if (Model.Enabled)
        {
            if (!Uri.TryCreate(Model.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                problems.Add("model.endpoint must be an absolute https URL.");
            if (string.IsNullOrWhiteSpace(Model.Model)) problems.Add("model.model must name a model.");
            if (Model.TimeoutMs < 1000) problems.Add("model.timeoutMs must be at least 1000.");
        }
        if (Workers.WallClockSeconds < 5) problems.Add("workers.wallClockSeconds must be at least 5.");
        if (Workers.MemoryMb < 64) problems.Add("workers.memoryMb must be at least 64.");
        if (!KeyChord.TryParse(Hotkeys.NoteKey, out var note, out var e1)) problems.Add($"hotkeys.noteKey: {e1}");
        if (!KeyChord.TryParse(Hotkeys.CommandKey, out var command, out var e2)) problems.Add($"hotkeys.commandKey: {e2}");
        if (note is not null && command is not null && note == command) problems.Add("hotkeys.noteKey and hotkeys.commandKey must differ.");
        if (FlowRelay.Enabled)
        {
            if (!KeyChord.TryParse(FlowRelay.HandsFreeChord, out var relay, out var e3)) problems.Add($"flowRelay.handsFreeChord: {e3}");
            else if (relay == note || relay == command) problems.Add("flowRelay.handsFreeChord must differ from both primary hotkeys.");
        }
        if (Capture.TranscriptTimeoutMs < 1000) problems.Add("capture.transcriptTimeoutMs must be at least 1000.");
        if (Capture.StabilizationMs < 100) problems.Add("capture.stabilizationMs must be at least 100.");
        if (Capture.StabilizationWithoutRelayMs < 100) problems.Add("capture.stabilizationWithoutRelayMs must be at least 100.");
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
    /// <summary>NOTE_KEY: starts or stops silent note capture.</summary>
    [JsonPropertyName("noteKey")] public string NoteKey { get; set; } = "F13";
    /// <summary>COMMAND_KEY: opens or closes an instruction turn.</summary>
    [JsonPropertyName("commandKey")] public string CommandKey { get; set; } = "F14";
}

public sealed class FlowRelaySettings
{
    /// <summary>
    /// When enabled, Relay emits exactly one fixed chord to toggle Wispr Flow hands-free mode
    /// after the capture surface is confirmed foreground. Off by default (contract §17.3).
    /// </summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("handsFreeChord")] public string HandsFreeChord { get; set; } = "Ctrl+Win+F24";
    /// <summary>Delay between focusing the capture surface and emitting the start chord.</summary>
    [JsonPropertyName("startDelayMs")] public int StartDelayMs { get; set; } = 200;
}

public sealed class CaptureSettings
{
    /// <summary>How long AWAITING_TRANSCRIPT waits for the first text before reporting a timeout.</summary>
    [JsonPropertyName("transcriptTimeoutMs")] public int TranscriptTimeoutMs { get; set; } = 10_000;
    /// <summary>Quiet period after the last text change before the transcript is considered complete (relay on).</summary>
    [JsonPropertyName("stabilizationMs")] public int StabilizationMs { get; set; } = 1_500;
    /// <summary>Quiet period when the relay is off and the user already finished dictating before pressing the key.</summary>
    [JsonPropertyName("stabilizationWithoutRelayMs")] public int StabilizationWithoutRelayMs { get; set; } = 600;
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

    /// <summary>off: instructions are recorded only. rules: deterministic command grammar. rules+model: grammar first, model gateway for the rest.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = Rules;
    /// <summary>Notes routed at or above this confidence are filed into the project automatically.</summary>
    [JsonPropertyName("autoRouteThreshold")] public double AutoRouteThreshold { get; set; } = 0.75;
    /// <summary>Notes between this and the auto threshold go to Review; below it they stay unrouted in staging.</summary>
    [JsonPropertyName("reviewThreshold")] public double ReviewThreshold { get; set; } = 0.35;
    [JsonPropertyName("planningTimeoutMs")] public int PlanningTimeoutMs { get; set; } = 60_000;
    [JsonPropertyName("maxToolCalls")] public int MaxToolCalls { get; set; } = 8;
}

public sealed class ModelSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>The only network endpoint the process may contact. OpenAI-compatible chat completions.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>Name of the DPAPI-protected secret holding the API key. Never stored in this file.</summary>
    [JsonPropertyName("secretName")] public string SecretName { get; set; } = "model-gateway";
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 30_000;
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; } = 1_500;
}

public sealed class WorkerSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("wallClockSeconds")] public int WallClockSeconds { get; set; } = 120;
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 512;
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
            if (validation.Any(p => p.StartsWith("flowRelay", StringComparison.Ordinal))) settings.FlowRelay = defaults.FlowRelay;
            if (validation.Any(p => p.StartsWith("capture", StringComparison.Ordinal))) settings.Capture = defaults.Capture;
            if (validation.Any(p => p.StartsWith("orchestrator", StringComparison.Ordinal))) settings.Orchestrator = defaults.Orchestrator;
            if (validation.Any(p => p.StartsWith("model", StringComparison.Ordinal))) settings.Model = defaults.Model;
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
