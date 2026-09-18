using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Composition;
using Relay.Core.Config;
using Relay.Core.Generation;
using Relay.Core.Judgments;
using Relay.Core.Model;
using Relay.Core.Privacy;
using Relay.Core.Storage;
using Relay.Core.Telemetry;
using Relay.Core.Time;
using Relay.Gateway;

namespace Relay.Desktop;

/// <summary>
/// Desktop host over <see cref="RelayComposition"/>: binds TypeSafe/model clients and problem reporting.
/// Does not construct SessionCoordinator / RelayRuntime.
/// </summary>
public sealed class CaseRelayHost : IDisposable
{
    private readonly RelayComposition _composition;
    private readonly TypeSafeJudgmentClient? _judgmentClient;
    private readonly IModelClient? _modelClient;
    private bool _disposed;

    private CaseRelayHost(
        RelayComposition composition,
        TypeSafeJudgmentClient? judgmentClient,
        IModelClient? modelClient,
        ISecretStore secrets,
        RelaySettings settings)
    {
        _composition = composition;
        _judgmentClient = judgmentClient;
        _modelClient = modelClient;
        Secrets = secrets;
        Settings = settings;
    }

    public DataRoot Root => _composition.Root;
    public CaseRuntime Runtime => _composition.Runtime;
    public IRelaySurface Surface => _composition.Surface;
    public CapabilityRegistry Capabilities => _composition.Capabilities;
    public HostedGrantStore Grants => _composition.Grants;
    public ISecretStore Secrets { get; }
    public RelaySettings Settings { get; }
    public IRelayTelemetry Telemetry => _composition.Telemetry;
    public string RunId => _composition.RunId;
    public string RunDir => _composition.RunDir;
    public string SessionId => _composition.SessionId;

    public static CaseRelayHost Create(DataRoot root, IClock clock, string version)
    {
        root.EnsureLayout(clock);
        var settings = SettingsStore.Load(root).Settings;
        var secrets = ModelComposition.Secrets(root);

        TypeSafeJudgmentClient? judgment = null;
        try
        {
            if (settings.Jev.Enabled)
                judgment = new TypeSafeJudgmentClient(settings.Jev, secrets);
        }
        catch (ArgumentException)
        {
            judgment = null;
        }

        IJudgmentClient judgmentClient = (IJudgmentClient?)judgment ?? new NullJudgmentClient();
        var modelClient = ModelComposition.Client(settings.Model, root);
        ITextGenerator? generator = modelClient is null
            ? null
            : new ModelTextGenerator(modelClient, settings.Model.MaxOutputTokens);

        var runId = Environment.GetEnvironmentVariable("RELAY_RUN_ID");
        var runDir = Environment.GetEnvironmentVariable("RELAY_RUN_DIR");

        var composition = RelayComposition.Create(
            root,
            clock,
            version,
            judgmentClient: judgmentClient,
            jevModel: settings.Jev.Model,
            generator: generator,
            settings: settings,
            modelHealth: () => modelClient is null
                ? ModelHealthView.Placeholder
                : new ModelHealthView(ModelHealthView.Ok, modelClient.Model),
            jevStatus: () => judgment is null
                ? HostedJudgmentView.Unavailable
                : settings.Jev.Enabled
                    ? HostedJudgmentView.Ready
                    : HostedJudgmentView.Disabled,
            runId: runId,
            runDir: runDir);

        return new CaseRelayHost(composition, judgment, modelClient, secrets, settings);
    }

    public void ReportCrash(string errorCode, string? caseId = null)
    {
        Telemetry.Emit(new ProductEventDraft
        {
            EventName = ProductEventNames.AppCrashed,
            Level = ProductEventLevels.Error,
            CaseId = caseId,
            ErrorCode = errorCode,
        });
    }

    public string ReportProblem(
        string whatHappened,
        string whatExpected,
        string severity,
        string? includeInputText = null,
        string? caseId = null,
        string? operationId = null,
        string? appVersion = null,
        string? commit = null)
    {
        var noteBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "user_problem_note",
            ["whatHappened"] = whatHappened,
            ["whatExpected"] = whatExpected,
            ["severity"] = severity,
            ["runId"] = RunId,
            ["appVersion"] = appVersion ?? "",
            ["commit"] = commit ?? Environment.GetEnvironmentVariable("RELAY_GIT_COMMIT") ?? "",
            ["caseId"] = caseId,
            ["operationId"] = operationId,
            ["includeInput"] = includeInputText is not null,
        };
        if (includeInputText is not null)
        {
            noteBody["inputCharCount"] = includeInputText.Length;
            var inputObj = Runtime.Objects.PutText(includeInputText, classification: SourceClassification.LocalOnly);
            noteBody["inputObjectId"] = inputObj.ObjectId;
            noteBody["inputSha256"] = inputObj.Sha256;
        }

        var stored = Runtime.Objects.PutJson(noteBody, classification: SourceClassification.LocalOnly);
        var lastSeq = _composition.Telemetry.LastSequence;
        var seqStart = Math.Max(1, lastSeq - 99);
        Telemetry.Emit(new ProductEventDraft
        {
            EventName = ProductEventNames.ProblemReported,
            Level = severity is "critical" or "error" ? ProductEventLevels.Error : ProductEventLevels.Warn,
            CaseId = caseId,
            OperationId = operationId,
            PayloadRef = stored.ObjectId,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userNoteRef"] = stored.ObjectId,
                ["userNoteSha256"] = stored.Sha256,
                ["severity"] = severity,
                ["expected"] = whatExpected.Length > 200 ? whatExpected[..200] : whatExpected,
                ["actual"] = whatHappened.Length > 200 ? whatHappened[..200] : whatHappened,
                ["eventSequenceStart"] = seqStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["eventSequenceEnd"] = Math.Max(lastSeq, seqStart).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["runId"] = RunId,
            },
        });

        return stored.ObjectId;
    }

    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < 8; i++)
        {
            var stepped = await Runtime.StepNextAsync(cancellationToken).ConfigureAwait(false);
            if (stepped is null) break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _composition.Dispose();
        _judgmentClient?.Dispose();
        (_modelClient as IDisposable)?.Dispose();
    }
}

/// <summary>Absent TypeSafe binding — engine waits rather than inventing judgments.</summary>
file sealed class NullJudgmentClient : IJudgmentClient
{
    public string ProviderName => "null";

    public Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken)
        => Task.FromResult(JudgmentResponse.FromFailure(
            JudgmentFailure.Create(JudgmentFailureCategories.Disabled, "No TypeSafe client bound.")));
}
