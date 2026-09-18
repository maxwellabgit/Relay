using System.Diagnostics;
using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Ids;
using Relay.Core.Privacy;
using Relay.Core.Time;

namespace Relay.Core.Judgments;

/// <summary>
/// Coordinates persist-before-dispatch and persist-before-decision for judgments.
/// Does not apply case decisions — that belongs to the decision engine (Phase 6).
/// </summary>
public sealed class JudgmentLifecycle
{
    private readonly JudgmentStore _store;
    private readonly JudgmentCache _cache;
    private readonly IJudgmentClient _client;
    private readonly IClock _clock;
    private readonly CaseStore? _cases;
    private readonly DisclosurePolicy? _disclosure;
    private readonly HostedGrantStore? _grants;

    public JudgmentLifecycle(
        JudgmentStore store,
        JudgmentCache cache,
        IJudgmentClient client,
        IClock clock,
        CaseStore? cases = null,
        DisclosurePolicy? disclosure = null,
        HostedGrantStore? grants = null)
    {
        _store = store;
        _cache = cache;
        _client = client;
        _clock = clock;
        _cases = cases;
        _disclosure = disclosure;
        _grants = grants;
    }

    public JudgmentStore Store => _store;
    public JudgmentCache Cache => _cache;

    /// <summary>
    /// Persist request, call provider unless a completed cache hit exists, persist response,
    /// optionally append a case event with audit metadata only.
    /// When <see cref="DisclosurePolicy"/> is configured, authorize before dispatch.
    /// </summary>
    public async Task<JudgmentLifecycleResult> ExecuteAsync(
        JudgmentRequest request,
        CancellationToken cancellationToken,
        bool appendCaseEvent = true,
        string? purpose = null,
        string? sessionId = null,
        string? projectId = null,
        int conservativeInputTokenEstimate = 256)
    {
        DisclosureDecision? disclosure = null;
        if (_disclosure is not null)
        {
            if (string.IsNullOrWhiteSpace(purpose))
            {
                return FailWithoutDispatch(
                    request,
                    JudgmentFailure.Create(JudgmentFailureCategories.Validation, "Disclosure purpose is required."));
            }

            try
            {
                disclosure = _disclosure.Authorize(
                    request,
                    purpose!,
                    _client.ProviderName,
                    sessionId,
                    projectId,
                    conservativeInputTokenEstimate);
                request = WithGrant(request, disclosure.Grant.GrantId);
            }
            catch (DisclosureException ex)
            {
                var category = ex.Category == "validation"
                    ? JudgmentFailureCategories.Validation
                    : JudgmentFailureCategories.NotAuthorized;
                return FailWithoutDispatch(request, JudgmentFailure.Create(category, ex.Message));
            }
        }

        var handle = _store.BeginRequest(request, _client.ProviderName);
        if (handle.AlreadyComplete)
        {
            var success = _store.TryLoadSuccess(handle.Record.JudgmentId)
                ?? throw new InvalidOperationException("Cached judgment missing success body.");
            return new JudgmentLifecycleResult(handle.Record, JudgmentResponse.FromSuccess(success), ProviderCalled: false);
        }

        if (appendCaseEvent)
            AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentRequested, handle.Record, disclosure?.Audit);

        var watch = Stopwatch.StartNew();
        JudgmentResponse response;
        try
        {
            response = await _client.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = _store.CompleteFailure(
                handle.Record.JudgmentId,
                JudgmentFailure.Create(JudgmentFailureCategories.Cancelled, "Judgment cancelled."));
            if (appendCaseEvent)
                AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentFailed, cancelled, disclosure?.Audit);
            throw;
        }

        response.Validate();
        JudgmentRecord terminal;
        if (response.Ok)
        {
            terminal = _store.CompleteSuccess(handle.Record.JudgmentId, response.Success!);
            if (_grants is not null && disclosure is not null && response.Success!.InputTokens > 0)
                _grants.RecordTokenUse(disclosure.Grant.GrantId, response.Success.InputTokens);
            if (appendCaseEvent)
                AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentCompleted, terminal, disclosure?.Audit);
        }
        else
        {
            terminal = _store.CompleteFailure(handle.Record.JudgmentId, response.Failure!);
            if (appendCaseEvent)
                AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentFailed, terminal, disclosure?.Audit);
        }

        _ = watch;
        return new JudgmentLifecycleResult(terminal, response, ProviderCalled: true);
    }

    /// <summary>
    /// After crash between response persistence and decision application: reuse completed body,
    /// do not call the provider again.
    /// </summary>
    public JudgmentLifecycleResult ResumeCompleted(string judgmentId)
    {
        var record = _store.TryGet(judgmentId)
            ?? throw new InvalidOperationException($"Unknown judgment '{judgmentId}'.");
        if (record.Status != JudgmentStatuses.Completed)
            throw new InvalidOperationException($"Judgment '{judgmentId}' is not completed.");
        var success = _store.TryLoadSuccess(judgmentId)
            ?? throw new InvalidOperationException($"Judgment '{judgmentId}' missing success body.");
        return new JudgmentLifecycleResult(record, JudgmentResponse.FromSuccess(success), ProviderCalled: false);
    }

    public IReadOnlyList<JudgmentRecord> ListRecoverable() => _store.ListUnresolved();

    private JudgmentLifecycleResult FailWithoutDispatch(JudgmentRequest request, JudgmentFailure failure)
    {
        _ = request;
        return new JudgmentLifecycleResult(
            new JudgmentRecord
            {
                JudgmentId = "undispatched",
                Provider = _client.ProviderName,
                QuestionSetId = request.QuestionSetId,
                QuestionSetVersion = request.QuestionSetVersion,
                Model = request.Model,
                Status = JudgmentStatuses.Failed,
                CaseId = request.CaseId,
                CaseVersion = request.CaseVersion,
                FailureCategory = failure.Category,
                CreatedAt = _clock.UtcNow,
                CompletedAt = _clock.UtcNow,
            },
            JudgmentResponse.FromFailure(failure),
            ProviderCalled: false);
    }

    private static JudgmentRequest WithGrant(JudgmentRequest request, string grantId) => new()
    {
        QuestionSetId = request.QuestionSetId,
        QuestionSetVersion = request.QuestionSetVersion,
        Model = request.Model,
        State = request.State,
        Questions = request.Questions,
        SourceObjectRefs = request.SourceObjectRefs,
        CaseId = request.CaseId,
        CaseVersion = request.CaseVersion,
        DisclosureGrantId = grantId,
        RequestHash = request.RequestHash,
        Provider = request.Provider,
    };

    private void AppendEventIfPossible(string? caseId, string eventType, JudgmentRecord record, DisclosureAudit? audit)
    {
        if (_cases is null || string.IsNullOrWhiteSpace(caseId)) return;
        var caseRecord = _cases.TryLoadRecord(caseId);
        if (caseRecord is null) return;

        var payloadObj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in JsonSerializer.SerializeToElement(JudgmentStore.ToAuditPayload(record), Storage.RelayJson.Compact).EnumerateObject())
            payloadObj[prop.Name] = prop.Value.Clone();
        if (audit is not null)
        {
            payloadObj["grantId"] = audit.GrantId;
            payloadObj["purpose"] = audit.Purpose;
            payloadObj["sourceHashes"] = audit.SourceHashes;
        }

        var payload = JsonSerializer.SerializeToElement(payloadObj, Storage.RelayJson.Compact);
        _cases.AppendEvent(new CaseEvent
        {
            EventId = Ulid.NewUlid(_clock.UtcNow),
            CaseId = caseId,
            CaseVersionAfter = caseRecord.Version,
            Type = eventType,
            Ts = _clock.UtcNow,
            Payload = payload,
        });
    }
}

public sealed record JudgmentLifecycleResult(
    JudgmentRecord Record,
    JudgmentResponse Response,
    bool ProviderCalled);
