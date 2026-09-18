using System.Diagnostics;
using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Ids;
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

    public JudgmentLifecycle(
        JudgmentStore store,
        JudgmentCache cache,
        IJudgmentClient client,
        IClock clock,
        CaseStore? cases = null)
    {
        _store = store;
        _cache = cache;
        _client = client;
        _clock = clock;
        _cases = cases;
    }

    public JudgmentStore Store => _store;
    public JudgmentCache Cache => _cache;

    /// <summary>
    /// Persist request, call provider unless a completed cache hit exists, persist response,
    /// optionally append a case event with audit metadata only.
    /// </summary>
    public async Task<JudgmentLifecycleResult> ExecuteAsync(
        JudgmentRequest request,
        CancellationToken cancellationToken,
        bool appendCaseEvent = true)
    {
        var handle = _store.BeginRequest(request, _client.ProviderName);
        if (handle.AlreadyComplete)
        {
            var success = _store.TryLoadSuccess(handle.Record.JudgmentId)
                ?? throw new InvalidOperationException("Cached judgment missing success body.");
            return new JudgmentLifecycleResult(handle.Record, JudgmentResponse.FromSuccess(success), ProviderCalled: false);
        }

        AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentRequested, handle.Record);

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
            AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentFailed, cancelled);
            throw;
        }

        response.Validate();
        JudgmentRecord terminal;
        if (response.Ok)
        {
            terminal = _store.CompleteSuccess(handle.Record.JudgmentId, response.Success!);
            AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentCompleted, terminal);
        }
        else
        {
            terminal = _store.CompleteFailure(handle.Record.JudgmentId, response.Failure!);
            AppendEventIfPossible(request.CaseId, CaseEventTypes.JudgmentFailed, terminal);
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

    private void AppendEventIfPossible(string? caseId, string eventType, JudgmentRecord record)
    {
        if (_cases is null || string.IsNullOrWhiteSpace(caseId)) return;
        var caseRecord = _cases.TryLoadRecord(caseId);
        if (caseRecord is null) return;

        var payload = JsonSerializer.SerializeToElement(JudgmentStore.ToAuditPayload(record), Storage.RelayJson.Compact);
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
