using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Time;

namespace Relay.Core.Storage;

/// <summary>
/// The application-owned data directory. Everything Relay is authoritative for in v0.1 lives
/// beneath this root; nothing here is user "content" in the project sense, so it sits in the
/// per-user application data directory rather than in Documents.
///
/// <code>
/// {root}\
///   relay.json                      data-root manifest (schema version, instance id)
///   config\settings.json            user settings (hotkeys, Flow relay, timeouts)
///   ledger\relay-ledger.jsonl       append-only, hash-linked event ledger (authoritative)
///   ledger\quarantine\              torn tail bytes preserved during ledger repair
///   sessions\{session-id}.json      session liveness records for crash detection
///   staging\drafts\current.json     the in-progress capture, rewritten as text arrives
///   staging\drafts\discarded\       interrupted drafts the user declined to keep
///   staging\notes\{note-id}.json    draft notes stored without model interpretation
///   incidents\                      failures that could not be written to the ledger
///   logs\                           redacted diagnostics (never capture text)
/// </code>
/// </summary>
public sealed class DataRoot
{
    public const int SchemaVersion = 1;
    public const string EnvironmentOverride = "RELAY_DATA_ROOT";

    public DataRoot(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }
    public string ManifestPath => Combine("relay.json");
    public string ConfigDirectory => Combine("config");
    public string SettingsPath => Combine("config", "settings.json");
    public string LedgerDirectory => Combine("ledger");
    public string LedgerPath => Combine("ledger", "relay-ledger.jsonl");
    public string LedgerQuarantineDirectory => Combine("ledger", "quarantine");
    public string SessionsDirectory => Combine("sessions");
    public string DraftsDirectory => Combine("staging", "drafts");
    public string CurrentDraftPath => Combine("staging", "drafts", "current.json");
    public string DiscardedDraftsDirectory => Combine("staging", "drafts", "discarded");
    public string DraftNotesDirectory => Combine("staging", "notes");
    public string IncidentsDirectory => Combine("incidents");
    public string LogsDirectory => Combine("logs");

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Default per-user location, honouring the RELAY_DATA_ROOT override.</summary>
    public static DataRoot Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentOverride);
        if (!string.IsNullOrWhiteSpace(overridePath)) return new DataRoot(overridePath);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new DataRoot(System.IO.Path.Combine(local, "Relay"));
    }

    /// <summary>Creates the directory layout and manifest if missing. Returns true when newly created.</summary>
    public bool EnsureLayout(IClock clock)
    {
        var created = !File.Exists(ManifestPath);
        foreach (var dir in new[]
        {
            Path, ConfigDirectory, LedgerDirectory, LedgerQuarantineDirectory, SessionsDirectory,
            DraftsDirectory, DiscardedDraftsDirectory, DraftNotesDirectory, IncidentsDirectory, LogsDirectory,
        })
        {
            Directory.CreateDirectory(dir);
        }

        if (created)
        {
            var manifest = new DataRootManifest(SchemaVersion, Ulid.NewUlid(clock.UtcNow), clock.UtcNow);
            AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, RelayJson.Indented));
        }
        return created;
    }

    public DataRootManifest? ReadManifest()
    {
        var text = AtomicFile.ReadAllTextIfExists(ManifestPath);
        return text is null ? null : JsonSerializer.Deserialize<DataRootManifest>(text, RelayJson.Indented);
    }
}

public sealed record DataRootManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public static class RelayJson
{
    /// <summary>Compact, deterministic serialization used for ledger lines and hashing.</summary>
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
