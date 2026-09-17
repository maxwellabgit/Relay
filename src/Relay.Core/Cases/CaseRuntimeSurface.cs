using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Evidence;
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
    private readonly Func<ServiceHealthView>? _serviceHealth;
    private readonly RetentionService? _retention;
    private readonly CapabilityBundleStore? _capabilities;
    private readonly CapabilityActivation? _activation;
    private string? _composerCaseId;
    private bool _hostedProcessingEnabled;
    private string? _hostedGrantId;

    public CaseRuntimeSurface(
        CaseRuntime runtime,
        IClock clock,
        Func<ModelHealthView>? modelHealth = null,
        Func<ServiceHealthView>? serviceHealth = null,
        RetentionService? retention = null,
        CapabilityBundleStore? capabilities = null,
        CapabilityActivation? activation = null)
    {
        _runtime = runtime;
        _clock = clock;
        _modelHealth = modelHealth;
        _serviceHealth = serviceHealth;
        _retention = retention;
        _capabilities = capabilities;
        _activation = activation;
    }

    public CaseRuntime Runtime => _runtime;

    public RelaySurfaceSnapshot Snapshot()
    {
        var feed = _runtime.Projections.ListFeedItems()
            .Select(f => new FeedProjectionItem(f.FeedId, f.CaseId, f.Ts, f.Text, f.Level))
            .ToList();

        var pending = new List<PendingApprovalView>();
        var cases = new List<CaseStatusView>();
        foreach (var caseId in _runtime.Cases.ListCaseIds())
        {
            var record = _runtime.GetCase(caseId);
            if (record is null) continue;
            cases.Add(new CaseStatusView(record.Id, record.Status, record.WaitingReason, record.Origin));
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
        var intake = _runtime.Intake.LoadState();
        var capture = new CaptureStatusView(
            intake.CaptureEnabled,
            intake.PendingMindSegmentIds.Count,
            intake.PendingWindowIds.Count,
            intake.HostedGrantId ?? _hostedGrantId);

        RetentionControlView? retentionView = null;
        if (_retention is not null)
        {
            var sessionId = intake.SessionId;
            double? sessionDays = null;
            if (sessionId is not null)
            {
                var (ttl, _) = _retention.GetSessionPolicy(sessionId);
                sessionDays = ttl.TotalDays;
            }
            retentionView = new RetentionControlView(30, sessionId, sessionDays);
        }

        var caps = new List<CapabilityView>();
        if (_capabilities is not null && Directory.Exists(_capabilities.BundlesDirectory))
        {
            foreach (var file in Directory.GetFiles(_capabilities.BundlesDirectory, "*.json"))
            {
                var b = _capabilities.TryLoad(Path.GetFileNameWithoutExtension(file));
                if (b is null) continue;
                caps.Add(new CapabilityView(b.BundleId, b.Name, b.Version, b.ContentHash, Active: false));
            }
        }

        return new RelaySurfaceSnapshot(
            feed,
            pending,
            _modelHealth?.Invoke() ?? ModelHealthView.Placeholder,
            Listening: listening is not null,
            ListeningCaseId: listening?.Id,
            ComposerCaseId: _composerCaseId,
            At: _clock.UtcNow,
            HostedProcessingEnabled: _hostedProcessingEnabled,
            HostedGrantId: _hostedGrantId ?? intake.HostedGrantId,
            Capture: capture,
            Cases: cases,
            ServiceHealth: _serviceHealth?.Invoke() ?? ServiceHealthView.UnavailablePlaceholder,
            Conflicts: [],
            Retention: retentionView,
            Capabilities: caps);
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

    public SurfaceResult SetHostedProcessing(bool enabled, string? grantId = null)
    {
        _hostedProcessingEnabled = enabled;
        _hostedGrantId = grantId;
        var state = _runtime.Intake.LoadState();
        state.HostedGrantId = grantId;
        _runtime.Intake.SaveState(state);
        return SurfaceResult.Success(enabled ? "Hosted processing enabled." : "Hosted processing disabled.", state.CaseId);
    }

    public SurfaceResult SubmitComposer(string text, string kind = CaseKind.Answer)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SurfaceResult.Fail("Composer text is required.");
        try
        {
            // Attach to a waiting case when answering clarification / pending ask.
            var waiting = _runtime.Cases.ListCaseIds()
                .Select(id => _runtime.GetCase(id))
                .Where(c => c is not null && c.Status == CaseStatus.Waiting)
                .Cast<CaseRecord>()
                .OrderByDescending(c =>
                    (c.WaitingReason?.Contains("clarif", StringComparison.OrdinalIgnoreCase) == true ? 2 : 0)
                    + (c.PendingWaits.Count > 0 ? 1 : 0))
                .FirstOrDefault();

            if (waiting is not null)
            {
                _composerCaseId = waiting.Id;
                _runtime.Projections.InsertFeedItem(
                    Relay.Core.Ids.Ulid.NewUlid(_clock.UtcNow),
                    waiting.Id,
                    _clock.UtcNow,
                    "Composer reply: " + text.Trim(),
                    "persistent");
                return SurfaceResult.Success("Attached reply to waiting case.", waiting.Id);
            }

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

    public SurfaceResult SetRetentionPolicy(string sessionId, double ttlDays, bool deleteExcerptsWithSession = false)
    {
        if (_retention is null) return SurfaceResult.Fail("retention_unavailable");
        _retention.SetSessionPolicy(sessionId, TimeSpan.FromDays(ttlDays), deleteExcerptsWithSession);
        return SurfaceResult.Success("Retention policy updated.", sessionId);
    }

    public SurfaceResult DeleteSessionNow(string sessionId, bool deleteExcerpts = false)
    {
        if (_retention is null) return SurfaceResult.Fail("retention_unavailable");
        var audit = _retention.DeleteSession(sessionId, immediate: true, deleteExcerpts: deleteExcerpts);
        return SurfaceResult.Success("Session deleted; audit " + audit.AuditId, sessionId);
    }

    public async Task<SurfaceResult> RunUntilIdleAsync(string? caseId = null, int maxSteps = 16, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = caseId ?? _composerCaseId;
            if (id is null)
            {
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
