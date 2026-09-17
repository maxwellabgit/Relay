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
    public string RegistryDirectory => Combine("registry");
    public string ProjectsRegistryPath => Combine("registry", "projects.json");
    public string WorkspacesPath => Combine("config", "workspaces.json");
    public string SecretsDirectory => Combine("config", "secrets");
    public string ArchiveDirectory => Combine("archive");
    public string BackupsDirectory => Combine("backups");
    public string ReviewDirectory => Combine("staging", "review");
    public string TurnsDirectory => Combine("staging", "turns");
    public string CurrentTurnPath => Combine("staging", "turns", "current.json");
    public string ExecutionsDirectory => Combine("staging", "executions");
    public string AgentsDirectory => Combine("staging", "agents");
    public string ProposalsDirectory => Combine("staging", "proposals");
    /// <summary>The live stream buffer window only; rewritten as the buffer changes and removed when the stream stops.</summary>
    public string StreamDirectory => Combine("staging", "stream");
    public string CurrentStreamPath => Combine("staging", "stream", "current.json");
    /// <summary>Selected conversation excerpts: the only place stream text outlives the buffer.</summary>
    public string ExcerptsDirectory => Combine("excerpts");
    /// <summary>Per-task diagnostics records.</summary>
    public string TasksDirectory => Combine("tasks");
    /// <summary>Every change Relay made to itself, with the before image.</summary>
    public string ChangeSetsDirectory => Combine("changesets");
    public string PreferencesPath => Combine("config", "preferences.json");
    public string PromptsDirectory => Combine("config", "prompts");
    /// <summary>Responses from approved external tasks, kept as source artifacts.</summary>
    public string ExternalArtifactsDirectory => Combine("artifacts", "external");
    /// <summary>Hits from the brokered web_search tool, kept as citable source artifacts.</summary>
    public string SearchArtifactsDirectory => Combine("artifacts", "search");
    /// <summary>Weights and thresholds of every decision the engine makes between paths.</summary>
    public string DecisionsPath => Combine("config", "decisions.json");
    /// <summary>One line per task: route, scores, model metrics, outcome, the user's response. Metadata only, for post-hoc tuning.</summary>
    public string UsageDirectory => Combine("usage");
    /// <summary>Tools Relay built and the user promoted: one JSON file per tool (manifest, source, tests), each written by a change set.</summary>
    public string ToolsDirectory => Combine("tools");
    /// <summary>Tool drafts that have not been promoted (or were rejected), and beneath them the sandbox runs of every test and call.</summary>
    public string ToolDraftsDirectory => Combine("staging", "tools");
    public string ToolRunsDirectory => Combine("staging", "tools", "runs");
    /// <summary>Workflows Relay authored and the user promoted: one JSON file per definition, each written by a change set.</summary>
    public string WorkflowsDirectory => Combine("workflows");
    /// <summary>Workflow drafts that have not been promoted (or were rejected).</summary>
    public string WorkflowDraftsDirectory => Combine("staging", "workflows");

    // --- vNext durable case runtime (additive) ---

    /// <summary>Authoritative per-case records and event logs: <c>cases/{caseId}/record.json</c> + <c>events.jsonl</c>.</summary>
    public string CasesDirectory => Combine("cases");
    /// <summary>Content-addressed object store (transcripts, prompts, tool packages). Referenced by id + sha256.</summary>
    public string ObjectsDirectory => Combine("objects");
    /// <summary>Rebuildable SQLite projections directory (feed, cases index, ready queue, approvals).</summary>
    public string ProjectionsDirectory => Combine("projections");
    /// <summary>SQLite file holding rebuildable projections.</summary>
    public string ProjectionsDatabasePath => Combine("projections", "projections.sqlite");
    /// <summary>Operation envelopes: <c>operations/{operationId}.json</c>.</summary>
    public string OperationsDirectory => Combine("operations");
    /// <summary>Isolated local harness runs: <c>.dev-runs/{run-id}/</c>.</summary>
    public string DevRunsDirectory => Combine(".dev-runs");

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
            RegistryDirectory, SecretsDirectory, ArchiveDirectory, BackupsDirectory, ReviewDirectory, TurnsDirectory,
            ExecutionsDirectory, AgentsDirectory, ProposalsDirectory,
            StreamDirectory, ExcerptsDirectory, TasksDirectory, ChangeSetsDirectory, PromptsDirectory, ExternalArtifactsDirectory, SearchArtifactsDirectory, UsageDirectory,
            ToolsDirectory, ToolDraftsDirectory, ToolRunsDirectory,
            WorkflowsDirectory, WorkflowDraftsDirectory,
            CasesDirectory, ObjectsDirectory, ProjectionsDirectory, OperationsDirectory, DevRunsDirectory,
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
