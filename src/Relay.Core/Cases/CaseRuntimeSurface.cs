using System.Text.Json;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// Adapts <see cref="CaseRuntime"/> to <see cref="IRelaySurface"/> so Desktop binds only to
/// projections and commands — not to orchestration internals.
/// </summary>
public sealed class CaseRuntimeSurface : IRelaySurface
{
    private readonly CaseRuntime _runtime;
    private readonly IClock _clock;
    private readonly Func<ModelHealthView>? _modelHealth;
    private string? _composerCaseId;

    public CaseRuntimeSurface(CaseRuntime runtime, IClock clock, Func<ModelHealthView>? modelHealth = null)
    {
        _runtime = runtime;
        _clock = clock;
        _modelHealth = modelHealth;
    }

    public CaseRuntime Runtime => _runtime;

    public RelaySurfaceSnapshot Snapshot()
    {
        var feed = _runtime.Projections.ListFeedItems()
            .Select(f => new FeedProjectionItem(f.FeedId, f.CaseId, f.Ts, f.Text, f.Level))
            .ToList();

        var pending = new List<PendingApprovalView>();
        foreach (var caseId in _runtime.Cases.ListCaseIds())
        {
            var record = _runtime.GetCase(caseId);
            if (record is null) continue;
            foreach (var opId in record.PendingOperationIds)
            {
                var op = _runtime.GetOperation(opId);
                if (op is null || op.Status != OperationStatus.AwaitingApproval) continue;
                pending.Add(new PendingApprovalView(
                    op.OperationId,
                    op.CaseId,
                    record.Version,
                    op.Capability,
                    op.CanonicalHashValue ?? op.CanonicalHash(),
                    TitleFor(op),
                    op.Arguments));
            }
        }

        var listening = _runtime.GetListeningCase();
        return new RelaySurfaceSnapshot(
            feed,
            pending,
            _modelHealth?.Invoke() ?? ModelHealthView.Placeholder,
            Listening: listening is not null,
            ListeningCaseId: listening?.Id,
            ComposerCaseId: _composerCaseId,
            At: _clock.UtcNow);
    }

    public SurfaceResult ToggleListening()
    {
        try
        {
            var current = _runtime.GetListeningCase();
            if (current is not null)
            {
                _runtime.StopListening();
                return SurfaceResult.Success("Listening stopped.", current.Id);
            }
            var started = _runtime.StartListening();
            return SurfaceResult.Success("Listening started.", started.Id);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public SurfaceResult SubmitComposer(string text, string kind = CaseKind.Answer)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SurfaceResult.Fail("Composer text is required.");
        try
        {
            var started = _runtime.StartDirectCase(text.Trim(), kind);
            _composerCaseId = started.Id;
            return SurfaceResult.Success("Submitted.", started.Id);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public SurfaceResult ApproveOperation(string operationId, string envelopeHash, long expectedCaseVersion)
    {
        try
        {
            var op = _runtime.ApproveOperation(operationId, envelopeHash, expectedCaseVersion);
            return SurfaceResult.Success($"Approved {op.Capability}.", op.CaseId, op.OperationId);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public SurfaceResult RejectOperation(string operationId, string? reason = null)
    {
        try
        {
            var op = _runtime.RejectOperation(operationId, reason);
            return SurfaceResult.Success($"Rejected {op.Capability}.", op.CaseId, op.OperationId);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public SurfaceResult EditOperation(string operationId, Dictionary<string, JsonElement> newArguments)
    {
        try
        {
            var op = _runtime.EditOperation(operationId, newArguments);
            return SurfaceResult.Success($"Edited {op.Capability}; new approval required.", op.CaseId, op.OperationId);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public SurfaceResult CancelCase(string caseId, string? reason = null)
    {
        try
        {
            var record = _runtime.CancelCase(caseId, reason);
            if (_composerCaseId == caseId) _composerCaseId = null;
            return SurfaceResult.Success("Case cancelled.", record.Id);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    public async Task<SurfaceResult> RunUntilIdleAsync(string? caseId = null, int maxSteps = 16, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = caseId ?? _composerCaseId;
            if (id is null)
            {
                // Drain ready queue once if no specific case.
                var stepped = await _runtime.StepNextAsync(cancellationToken).ConfigureAwait(false);
                return stepped is null
                    ? SurfaceResult.Success("Idle.")
                    : SurfaceResult.Success($"Stepped {stepped.Id}.", stepped.Id);
            }
            var record = await _runtime.RunUntilIdleAsync(id, maxSteps, cancellationToken).ConfigureAwait(false);
            return SurfaceResult.Success($"Case {record.Id} is {record.Status}.", record.Id);
        }
        catch (Exception ex)
        {
            return SurfaceResult.Fail(ex.Message);
        }
    }

    private static string TitleFor(OperationEnvelope op)
    {
        if (op.Arguments.TryGetValue("name", out var name) && name.ValueKind == JsonValueKind.String)
            return $"{op.Capability}: {name.GetString()}";
        return op.Capability;
    }
}
