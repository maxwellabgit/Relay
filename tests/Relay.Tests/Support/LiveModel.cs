using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Gateway;

namespace Relay.Tests.Support;

/// <summary>
/// The real model a live test talks to, read from the environment so nothing about it is committed:
/// RELAY_LIVE_MODEL_KEY switches live tests on (any non-empty value; a loopback server needs no key, the
/// variable is the switch), RELAY_LIVE_MODEL_ENDPOINT and RELAY_LIVE_MODEL name the endpoint and model,
/// and RELAY_LIVE_REPORT_DIR, when set, receives the reports and transcripts so a run can be read after
/// the temp roots are gone. <see cref="FromEnvironment"/> is null when the switch is off: live tests then
/// return without running, which is what CI sees.
/// </summary>
public sealed class LiveModel
{
    public const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string DefaultModel = "gpt-4o-mini";
    private const int TimeoutMs = 180_000;

    private LiveModel(string key, string endpoint, string model, string? reportDirectory)
    {
        Key = key;
        Endpoint = endpoint;
        Model = model;
        ReportDirectory = reportDirectory;
    }

    public string Key { get; }
    public string Endpoint { get; }
    public string Model { get; }
    public string? ReportDirectory { get; }
    public bool IsLoopback => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static LiveModel? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var reportDirectory = Environment.GetEnvironmentVariable("RELAY_LIVE_REPORT_DIR");
        return new LiveModel(key,
            Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_ENDPOINT") ?? DefaultEndpoint,
            Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL") ?? DefaultModel,
            string.IsNullOrWhiteSpace(reportDirectory) ? null : reportDirectory);
    }

    public ModelSettings Settings => new() { Enabled = true, Endpoint = Endpoint, Model = Model, SecretName = "live", TimeoutMs = TimeoutMs };

    /// <summary>The gateway client Relay itself uses. The key is stored only for a remote endpoint; loopback sends none.</summary>
    public OpenAiCompatibleClient Client()
    {
        var secrets = new MemorySecretStore();
        if (!IsLoopback) secrets.Set("live", Key);
        return new OpenAiCompatibleClient(Settings, secrets);
    }

    /// <summary>Settings for a scenario that runs with this model as RELAY0. Listening is left to the test: dictation needs it off while the world is built.</summary>
    public void Configure(RelaySettings cfg)
    {
        cfg.Orchestrator.Mode = OrchestratorSettings.Mind;
        cfg.Orchestrator.StepTimeoutMs = TimeoutMs;
        cfg.Model.Enabled = true;
        cfg.Model.Endpoint = Endpoint;
        cfg.Model.Model = Model;
        cfg.Model.TimeoutMs = TimeoutMs;
        cfg.Listening.PassTimeoutMs = TimeoutMs;
    }

    /// <summary>Writes a report file into the report directory when one is configured; returns its path, or null when nothing was written.</summary>
    public string? Write(string fileName, string content)
    {
        if (ReportDirectory is null) return null;
        Directory.CreateDirectory(ReportDirectory);
        var path = Path.Combine(ReportDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
