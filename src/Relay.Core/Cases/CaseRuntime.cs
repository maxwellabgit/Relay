using System.Diagnostics;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// The one decision loop for durable cases. Persists every case event before dispatching
/// consequences. Clean shutdown suspends; restart reconstructs from stores.
/// </summary>
public sealed class CaseRuntime : IDisposable
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly ICaseMind _mind;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly CaseStore _cases;
    private readonly OperationStore _operations;
    private readonly ObjectStore _objects;
    private readonly ProjectionDatabase _projections;
    private readonly ReadyQueue _ready;
    private readonly InferenceLease _inference;
    private readonly Action _onSideEffect;
    private readonly string _leaseOwner;
    private readonly object _gate = new();
    private bool _disposed;

    private CaseRuntime(
        DataRoot root,
        IClock clock,
        ICaseMind mind,
        RuntimeDiagnostics diagnostics,
        Action? onSideEffect)
    {
        _root = root;
        _clock = clock;
        _mind = mind;
        _diagnostics = diagnostics;
        _onSideEffect = onSideEffect ?? (() => { });
        _cases = new CaseStore(root);
        _operations = new OperationStore(root);
        _objects = new ObjectStore(root, clock);
        _projections = ProjectionDatabase.Open(root);
        _ready = new ReadyQueue(_projections, clock);
        _inference = new InferenceLease();
        _leaseOwner = Ulid.NewUlid(clock.UtcNow);
    }

    public CaseStore Cases => _cases;
    public OperationStore Operations => _operations;
    public ObjectStore Objects => _objects;
    public ReadyQueue Ready => _ready;
    public ProjectionDatabase Projections => _projections;
    public int SideEffectCount { get; private set; }

    /// <summary>Opens a runtime on an existing data root, reconstructing suspended work.</summary>
    public static CaseRuntime Open(
        DataRoot root,
        IClock clock,
        ICaseMind mind,
        RuntimeDiagnostics diagnostics,
        Action? onSideEffect = null)
    {
        root.EnsureLayout(clock);
        var runtime = new CaseRuntime(root, clock, mind, diagnostics, onSideEffect);
        runtime.Reconstruct();
        diagnostics.Write(clock.UtcNow, "info", "CaseRuntime", "opened", status: "ok");
        return runtime;
    }

    private void Reconstruct()
    {
        foreach (var caseId in _cases.ListCaseIds())
        {
            var record = _cases.TryLoadRecord(caseId);
            if (record is null) continue;
            _projections.UpsertCase(record);

            if (record.Status == CaseStatus.Suspended)
            {
                // Suspended cases with pending approvals stay waiting; others return to ready.
                var hasAwaiting = record.PendingOperationIds
                    .Select(id => _operations.TryLoad(id))
                    .Any(op => op is { Status: OperationStatus.AwaitingApproval });

                if (hasAwaiting)
                {
                    record.Status = CaseStatus.Waiting;
                    record.UpdatedAt = _clock.UtcNow;
                    PersistRecord(record);
                    _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "resumed_waiting",
                        caseId: record.Id, caseVersion: record.Version, status: record.Status);
                }
                else if (record.Status != CaseStatus.Completed && record.Status != CaseStatus.Cancelled)
                {
                    record.Status = CaseStatus.Active;
                    record.UpdatedAt = _clock.UtcNow;
                    PersistRecord(record);
                    _ready.TryEnqueue(record.Id, ReadyPriority.UserReplyOrApproval);
                    AppendEvent(record, CaseEventTypes.CaseResumed, new { reason = "restart" });
                    _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "resumed_ready",
                        caseId: record.Id, caseVersion: record.Version, status: record.Status);
                }
            }
        }

        foreach (var op in _operations.ListAll())
            _projections.UpsertOperation(op);
    }

    public CaseRecord StartDirectCase(string objective, string kind = CaseKind.Answer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        lock (_gate)
        {
            var now = _clock.UtcNow;
            var id = Ulid.NewUlid(now);
            var record = new CaseRecord
            {
                Id = id,
                Version = 0,
                Origin = CaseOrigin.Direct,
                Kind = kind,
                ApprovedObjective = objective,
                Status = CaseStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                AllowedCapabilities = [ScriptedCaseMind.DefaultCapability],
            };

            PersistRecord(record);
            var evt = AppendEvent(record, CaseEventTypes.UserInput, new { text = objective, origin = CaseOrigin.Direct });
            record.ProcessedEventIds.Add(evt.EventId);
            PersistRecord(record);

            _ready.TryEnqueue(record.Id, ReadyPriority.NewDirectRequest);
            _diagnostics.Write(now, "info", "CaseRuntime", "case_started",
                caseId: record.Id, caseVersion: record.Version, status: record.Status);
            return Clone(record);
        }
    }

    /// <summary>Dequeues one ready case (if any) and steps it once under the inference lease.</summary>
    public async Task<CaseRecord?> StepNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = _ready.TryDequeue(_leaseOwner);
        if (lease is null) return null;
        try
        {
            return await StepCaseAsync(lease.CaseId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ready.Complete(lease.CaseId);
        }
    }

    public async Task<CaseRecord> StepCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();

        await _inference.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                var record = _cases.TryLoadRecord(caseId)
                    ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
                if (record.Status is CaseStatus.Completed or CaseStatus.Cancelled)
                    return Clone(record);

                var events = _cases.LoadEvents(caseId);
                var pendingOps = record.PendingOperationIds
                    .Select(id => _operations.TryLoad(id))
                    .Where(op => op is not null)
                    .Cast<OperationEnvelope>()
                    .ToList();

                var request = new CaseMindRequest(
                    record.Id,
                    record.Origin,
                    record.Kind,
                    record.ApprovedObjective,
                    record.Version,
                    record.Status,
                    events.TakeLast(32).ToList(),
                    record.PendingOperationIds,
                    pendingOps,
                    _clock.UtcNow,
                    record.Budgets.StepsUsed);

                // Step the mind synchronously inside the lock for Slice 1 determinism.
                CaseMindStep step;
                try
                {
                    step = _mind.StepAsync(request, cancellationToken).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _diagnostics.Write(_clock.UtcNow, "error", "CaseRuntime", "mind_failed",
                        caseId: record.Id, caseVersion: record.Version, error: ex.Message, latencyMs: sw.ElapsedMilliseconds);
                    throw;
                }

                record.Budgets.StepsUsed++;
                var stepEvt = AppendEvent(record, CaseEventTypes.MindStepped, new
                {
                    mind = _mind.Name,
                    move = step.Move,
                    feed = step.Feed,
                    read = step.Read,
                });
                record.ProcessedEventIds.Add(stepEvt.EventId);

                ApplyMove(record, step, stepEvt.EventId);
                PersistRecord(record);

                _projections.InsertFeedItem(Ulid.NewUlid(_clock.UtcNow), record.Id, _clock.UtcNow, step.Feed, "ambient");
                _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "case_stepped",
                    caseId: record.Id, caseVersion: record.Version, status: record.Status, latencyMs: sw.ElapsedMilliseconds);

                return Clone(record);
            }
        }
        finally
        {
            _inference.Release();
        }
    }

    private void ApplyMove(CaseRecord record, CaseMindStep step, string causedByEventId)
    {
        switch (step.Move.Type)
        {
            case CaseMove.Propose:
                ProposeOperation(record, step.Move, causedByEventId);
                break;
            case CaseMove.Wait:
                record.Status = CaseStatus.Waiting;
                if (!record.PendingWaits.Contains(step.Move.Text))
                    record.PendingWaits.Add(step.Move.Text);
                AppendEvent(record, CaseEventTypes.WaitEntered, new { reason = step.Move.Text });
                break;
            case CaseMove.Stop:
            case CaseMove.Say when step.Move.Done:
                record.Status = CaseStatus.Completed;
                record.Result = step.Move.Text;
                AppendEvent(record, CaseEventTypes.CaseCompleted, new { result = step.Move.Text });
                break;
            case CaseMove.Say:
                // Narration only; stay active / re-enqueue if caller wants another step.
                break;
            default:
                // Slice 1 ignores other move types.
                break;
        }
    }

    private void ProposeOperation(CaseRecord record, CaseMove move, string causedByEventId)
    {
        var capability = move.Name;
        if (move.Args.TryGetValue("capability", out var capEl) && capEl.ValueKind == JsonValueKind.String)
            capability = capEl.GetString() ?? capability;

        var capabilityVersion = 1;
        if (move.Args.TryGetValue("capabilityVersion", out var verEl) && verEl.TryGetInt32(out var v))
            capabilityVersion = v;

        var idempotencyKey = ScriptedCaseMind.DefaultIdempotencyKey;
        if (move.Args.TryGetValue("idempotencyKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            idempotencyKey = keyEl.GetString() ?? idempotencyKey;

        // Duplicate propose with same idempotency key is a no-op if already present.
        var existing = _operations.TryFindByIdempotencyKey(idempotencyKey);
        if (existing is not null && existing.CaseId == record.Id)
        {
            AppendEvent(record, CaseEventTypes.DuplicateIgnored, new { operationId = existing.OperationId, idempotencyKey });
            record.Status = CaseStatus.Waiting;
            return;
        }

        var now = _clock.UtcNow;
        var opId = Ulid.NewUlid(now);
        var envelope = new OperationEnvelope
        {
            OperationId = opId,
            CaseId = record.Id,
            CaseVersion = record.Version,
            CausedByEventId = causedByEventId,
            Capability = capability,
            CapabilityVersion = capabilityVersion,
            Arguments = new Dictionary<string, JsonElement>(move.Args, StringComparer.Ordinal),
            IdempotencyKey = idempotencyKey,
            Status = OperationStatus.AwaitingApproval,
        };
        envelope.CanonicalHashValue = envelope.CanonicalHash();
        _operations.Save(envelope);
        _projections.UpsertOperation(envelope);

        if (!record.PendingOperationIds.Contains(opId))
            record.PendingOperationIds.Add(opId);
        record.Status = CaseStatus.Waiting;

        var evt = AppendEvent(record, CaseEventTypes.OperationProposed, new
        {
            operationId = opId,
            capability,
            idempotencyKey,
            canonicalHash = envelope.CanonicalHashValue,
        });
        record.ProcessedEventIds.Add(evt.EventId);

        _diagnostics.Write(now, "info", "CaseRuntime", "operation_proposed",
            caseId: record.Id, caseVersion: record.Version, operationId: opId, status: envelope.Status);
    }

    /// <summary>Binds approval to the envelope's canonical hash. Requires matching case version.</summary>
    public OperationEnvelope ApproveOperation(string operationId, string envelopeHash, long expectedCaseVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");

            if (record.Version != expectedCaseVersion)
                throw new InvalidOperationException($"Case version mismatch: expected {expectedCaseVersion}, actual {record.Version}.");

            var currentHash = envelope.CanonicalHash();
            if (!string.Equals(currentHash, envelopeHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Envelope hash mismatch; payload changed since proposal.");

            if (envelope.Status == OperationStatus.Approved || envelope.Status is OperationStatus.Executing or OperationStatus.Completed)
                return envelope;

            if (envelope.Status != OperationStatus.AwaitingApproval)
                throw new InvalidOperationException($"Operation status '{envelope.Status}' cannot be approved.");

            var now = _clock.UtcNow;
            var approvalId = Ulid.NewUlid(now);
            envelope.ApprovalId = approvalId;
            envelope.Status = OperationStatus.Approved;
            envelope.CanonicalHashValue = currentHash;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);
            _projections.UpsertApproval(approvalId, operationId, record.Id, currentHash, now, "approved");

            var evt = AppendEvent(record, CaseEventTypes.OperationApproved, new
            {
                operationId,
                approvalId,
                envelopeHash = currentHash,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            record.PendingWaits.RemoveAll(w => w.Contains(operationId, StringComparison.Ordinal));
            record.Status = CaseStatus.Active;
            PersistRecord(record);

            _ready.TryEnqueue(record.Id, ReadyPriority.UserReplyOrApproval);
            _diagnostics.Write(now, "info", "CaseRuntime", "operation_approved",
                caseId: record.Id, caseVersion: record.Version, operationId: operationId, status: envelope.Status);
            return envelope;
        }
    }

    /// <summary>
    /// Executes an approved operation exactly once. Side effects are gated by status transition
    /// and by idempotency key — a second call is a no-op.
    /// </summary>
    public OperationEnvelope ExecuteOperation(string operationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");

            // Idempotency: already executed/completed → no new side effect.
            if (envelope.Status is OperationStatus.Executing or OperationStatus.Completed)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new { operationId, reason = "already_executed" });
                PersistRecord(record);
                return envelope;
            }

            var peer = _operations.TryFindByIdempotencyKey(envelope.IdempotencyKey);
            if (peer is not null && peer.OperationId != envelope.OperationId &&
                peer.Status is OperationStatus.Executing or OperationStatus.Completed)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    peerOperationId = peer.OperationId,
                    idempotencyKey = envelope.IdempotencyKey,
                });
                PersistRecord(record);
                return envelope;
            }

            if (envelope.Status != OperationStatus.Approved)
                throw new InvalidOperationException($"Operation status '{envelope.Status}' cannot be executed.");

            // Case version check: envelope must still match the case version it was proposed against,
            // or have been re-bound. For Slice 1 we require equality with current case version at propose time
            // was stamped; after events the version advanced — require the envelope's caseVersion matches
            // the version when proposed (already stored). Additional approve-time check already ran.
            // On execute we only require the case is not cancelled/completed.
            if (record.Status is CaseStatus.Cancelled or CaseStatus.Completed)
                throw new InvalidOperationException($"Case '{record.Id}' is {record.Status}; refusing execute.");

            envelope.Status = OperationStatus.Executing;
            _operations.Save(envelope);

            // Side effect — exactly once on this transition.
            SideEffectCount++;
            envelope.SideEffectCount++;
            _onSideEffect();

            var result = _objects.PutJson(new { ok = true, sideEffectCount = envelope.SideEffectCount, at = _clock.UtcNow });
            envelope.ResultRef = result.ObjectId;
            envelope.Status = OperationStatus.Completed;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);

            var evt = AppendEvent(record, CaseEventTypes.OperationExecuted, new
            {
                operationId,
                resultRef = result.ObjectId,
                sha256 = result.Sha256,
                sideEffectCount = envelope.SideEffectCount,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            record.PendingOperationIds.Remove(operationId);
            record.Status = CaseStatus.Active;
            PersistRecord(record);

            _ready.TryEnqueue(record.Id, ReadyPriority.ToolOrDelegateCompletion);
            _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "operation_executed",
                caseId: record.Id, caseVersion: record.Version, operationId: operationId,
                status: envelope.Status, resultRef: result.ObjectId);
            return envelope;
        }
    }

    /// <summary>
    /// Marks an operation completed (or records a duplicate completion as a no-op when the
    /// idempotency key already finished). Does not re-run side effects.
    /// </summary>
    public OperationEnvelope CompleteOperation(string operationId, object? result = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");

            if (envelope.Status == OperationStatus.Completed)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    idempotencyKey = envelope.IdempotencyKey,
                    reason = "duplicate_completion",
                });
                PersistRecord(record);
                _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "duplicate_completion_ignored",
                    caseId: record.Id, caseVersion: record.Version, operationId: operationId, status: envelope.Status);
                return envelope;
            }

            var peer = _operations.TryFindByIdempotencyKey(envelope.IdempotencyKey);
            if (peer is not null && peer.Status == OperationStatus.Completed && peer.OperationId != envelope.OperationId)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    peerOperationId = peer.OperationId,
                    idempotencyKey = envelope.IdempotencyKey,
                    reason = "idempotency_key_already_completed",
                });
                PersistRecord(record);
                return envelope;
            }

            if (result is not null)
            {
                var stored = _objects.PutJson(result);
                envelope.ResultRef = stored.ObjectId;
            }

            envelope.Status = OperationStatus.Completed;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);

            var evt = AppendEvent(record, CaseEventTypes.OperationCompleted, new
            {
                operationId,
                resultRef = envelope.ResultRef,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            PersistRecord(record);
            return envelope;
        }
    }

    /// <summary>Clean shutdown: every non-terminal case becomes suspended; ready queue is preserved.</summary>
    public void SuspendAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            foreach (var caseId in _cases.ListCaseIds())
            {
                var record = _cases.TryLoadRecord(caseId);
                if (record is null) continue;
                if (record.Status is CaseStatus.Completed or CaseStatus.Cancelled or CaseStatus.Suspended)
                    continue;

                record.Status = CaseStatus.Suspended;
                record.UpdatedAt = _clock.UtcNow;
                AppendEvent(record, CaseEventTypes.CaseSuspended, new { reason = "clean_shutdown" });
                PersistRecord(record);
                _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "case_suspended",
                    caseId: record.Id, caseVersion: record.Version, status: record.Status);
            }
        }
    }

    public CaseRecord? GetCase(string caseId) => _cases.TryLoadRecord(caseId);
    public OperationEnvelope? GetOperation(string operationId) => _operations.TryLoad(operationId);

    public OperationEnvelope? GetPendingApproval(string caseId)
    {
        var record = _cases.TryLoadRecord(caseId);
        if (record is null) return null;
        foreach (var id in record.PendingOperationIds)
        {
            var op = _operations.TryLoad(id);
            if (op?.Status == OperationStatus.AwaitingApproval) return op;
        }
        return null;
    }

    private CaseEvent AppendEvent(CaseRecord record, string type, object payload)
    {
        record.Version++;
        record.UpdatedAt = _clock.UtcNow;
        var evt = new CaseEvent
        {
            EventId = Ulid.NewUlid(_clock.UtcNow),
            CaseId = record.Id,
            CaseVersionAfter = record.Version,
            Type = type,
            Ts = _clock.UtcNow,
            CausationId = record.ProcessedEventIds.LastOrDefault(),
            Payload = JsonSerializer.SerializeToElement(payload, RelayJson.Compact),
        };
        _cases.AppendEvent(evt);
        return evt;
    }

    private void PersistRecord(CaseRecord record)
    {
        record.UpdatedAt = _clock.UtcNow;
        _cases.SaveRecord(record);
        _projections.UpsertCase(record);
    }

    private static CaseRecord Clone(CaseRecord r)
        => JsonSerializer.Deserialize<CaseRecord>(JsonSerializer.Serialize(r, RelayJson.Compact), RelayJson.Compact)!;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inference.Dispose();
        _projections.Dispose();
    }
}
