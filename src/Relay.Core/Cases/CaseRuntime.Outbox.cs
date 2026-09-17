using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Listening;
using Relay.Core.Policy;

namespace Relay.Core.Cases;

public sealed partial class CaseRuntime
{
    public async Task DispatchPendingCommandsAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<string> ids;
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId);
            if (record is null) return;
            ids = record.PendingCommandIds.ToList();
        }
        await DispatchCommandIdsAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an approved operation as executing and runs the broker OUTSIDE the case lock.
    /// Refuses when the case was cancelled before dispatch started.
    /// </summary>
    public OperationEnvelope DispatchOperation(string operationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OperationEnvelope envelope;
        lock (_gate)
        {
            envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");

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

            if (record.Status == CaseStatus.Completed)
                throw new InvalidOperationException($"Case '{record.Id}' is completed; refusing dispatch.");

            // Never dispatch not-yet-started ops after cancellation.
            if (record.Status == CaseStatus.Cancelled)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    reason = "cancelled_before_dispatch",
                });
                PersistRecord(record);
                envelope.Status = OperationStatus.Cancelled;
                _operations.Save(envelope);
                _projections.UpsertOperation(envelope);
                return envelope;
            }

            if (envelope.Status != OperationStatus.Approved)
                throw new InvalidOperationException($"Operation status '{envelope.Status}' cannot be dispatched.");

            envelope.Status = OperationStatus.Executing;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);
            AppendEvent(record, CaseEventTypes.OperationExecuted, new
            {
                operationId,
                phase = "dispatched",
                capability = envelope.Capability,
            });
            PersistRecord(record);
        }

        // Side effects run outside the case lock.
        OperationApplyResult applied;
        if (_researchStallHook is not null
            && envelope.Capability is ResearchCapabilities.Search or ResearchCapabilities.Delegate)
        {
            _researchStallHook(new RuntimeCommand
            {
                CommandId = "stall:" + envelope.OperationId,
                CaseId = envelope.CaseId,
                Kind = RuntimeCommandKinds.DispatchAuthorizedOperation,
                CreatedAt = _clock.UtcNow,
                OperationId = envelope.OperationId,
            }, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (_tools is not null && _tools.Handles(envelope.Capability))
            applied = _tools.ApplyAsync(envelope).GetAwaiter().GetResult();
        else if (_research is not null && _research.Handles(envelope.Capability))
            applied = _research.ApplyAsync(envelope).GetAwaiter().GetResult();
        else
            applied = ApplyOperationIdempotent(envelope);

        return AcceptOperationResult(operationId, applied);
    }

    /// <summary>
    /// Ingests an operation result. Late results after cancel are stored for audit without reopening the case.
    /// </summary>
    public OperationEnvelope AcceptOperationResult(string operationId, OperationApplyResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");
            var caseWasCancelled = record.Status == CaseStatus.Cancelled;

            if (envelope.Status == OperationStatus.Completed)
            {
                AppendEvent(record, CaseEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    idempotencyKey = envelope.IdempotencyKey,
                    reason = "duplicate_completion",
                });
                PersistRecord(record);
                return envelope;
            }

            // Objective revision invalidates older in-flight results.
            if (envelope.CaseVersion < record.ObjectiveRevision && !caseWasCancelled)
            {
                AppendEvent(record, CaseDomainEventTypes.DuplicateIgnored, new
                {
                    operationId,
                    reason = "objective_revision_stale",
                    objectiveRevision = record.ObjectiveRevision,
                    envelopeCaseVersion = envelope.CaseVersion,
                });
                envelope.Status = OperationStatus.Failed;
                envelope.ResultRef = result.ResultRef;
                _operations.Save(envelope);
                _projections.UpsertOperation(envelope);
                PersistRecord(record);
                return envelope;
            }

            if (!result.Ok)
            {
                envelope.Status = OperationStatus.Failed;
                _operations.Save(envelope);
                _projections.UpsertOperation(envelope);
                AppendEvent(record, CaseEventTypes.OperationFailed, new
                {
                    operationId,
                    error = result.Error,
                    stale = caseWasCancelled,
                });
                PersistRecord(record);
                if (caseWasCancelled) return envelope;
                throw new InvalidOperationException(result.Error ?? result.Summary);
            }

            // Stable operation-derived object identity avoids duplicate writes on resume.
            var resultRef = result.ResultRef ?? envelope.ResultRef;
            if (resultRef is null)
            {
                var stored = _objects.PutJson(
                    new { ok = true, operationId, summary = result.Summary },
                    objectId: "op-result:" + operationId);
                resultRef = stored.ObjectId;
            }

            if (!caseWasCancelled && envelope.SideEffectCount == 0)
            {
                SideEffectCount++;
                envelope.SideEffectCount = 1;
            }
            envelope.ResultRef = resultRef;
            envelope.Status = OperationStatus.Completed;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);

            object? resultBlob = null;
            if (envelope.ResultRef is not null)
            {
                try
                {
                    var read = CaseTools.ReadArtifactResult(_objects, envelope.ResultRef);
                    var bodyProp = read.GetType().GetProperty("body");
                    var body = bodyProp?.GetValue(read) as string;
                    if (body is not null)
                        resultBlob = JsonSerializer.Deserialize<JsonElement>(body);
                }
                catch { /* best effort */ }
            }

            var evt = AppendEvent(record, caseWasCancelled ? CaseDomainEventTypes.LateResultAccepted : CaseEventTypes.OperationExecuted, new
            {
                operationId,
                capability = envelope.Capability,
                resultRef = envelope.ResultRef,
                sideEffectCount = envelope.SideEffectCount,
                summary = result.Summary,
                result = resultBlob,
                stale = caseWasCancelled,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            record.PendingOperationIds.Remove(operationId);

            if (caseWasCancelled)
            {
                record.Status = CaseStatus.Cancelled;
                PersistRecord(record);
                _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "late_result_accepted",
                    caseId: record.Id, caseVersion: record.Version, operationId: operationId, status: record.Status);
                return envelope;
            }

            record.Status = CaseStatus.Active;
            PersistRecord(record);
            _ready.TryEnqueue(record.Id, ReadyPriority.ToolOrDelegateCompletion);
            Feed(record.Id, result.Summary, "persistent");
            _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "operation_result_accepted",
                caseId: record.Id, caseVersion: record.Version, operationId: operationId,
                status: envelope.Status, resultRef: envelope.ResultRef);
            return envelope;
        }
    }

    /// <summary>
    /// Backward-compatible: dispatch then accept. After cancel, does not run the executor.
    /// </summary>
    public OperationEnvelope ExecuteOperation(string operationId)
        => DispatchOperation(operationId);

    /// <summary>Revises the approved objective without advancing as a substitute for event version.</summary>
    public CaseRecord ReviseObjective(string caseId, string newObjective)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId)
                ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
            record.ObjectiveRevision++;
            record.ApprovedObjective = newObjective;
            AppendEvent(record, CaseDomainEventTypes.ObjectiveRevised, new
            {
                objective = newObjective,
                objectiveRevision = record.ObjectiveRevision,
            });
            PersistRecord(record);
            return Clone(record);
        }
    }

    /// <summary>Sets decision dependency refs used to decide whether a segment invalidates work.</summary>
    public CaseRecord SetDecisionDependencies(string caseId, IEnumerable<string> refs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId)
                ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
            record.DecisionDependencyRefs = refs.ToList();
            AppendEvent(record, CaseDomainEventTypes.DecisionDepsSet, new { refs = record.DecisionDependencyRefs });
            PersistRecord(record);
            return Clone(record);
        }
    }

    /// <summary>
    /// Returns whether an ingested segment id is relevant to the case's decision dependencies.
    /// Unrelated segments do not invalidate still-relevant decisions.
    /// </summary>
    public bool SegmentInvalidatesDecision(string caseId, string segmentId)
    {
        var record = _cases.TryLoadRecord(caseId);
        if (record is null) return false;
        if (record.DecisionDependencyRefs.Count == 0) return false;
        var needle = "segment:" + segmentId;
        return record.DecisionDependencyRefs.Any(r =>
            string.Equals(r, needle, StringComparison.Ordinal)
            || r.Contains(segmentId, StringComparison.Ordinal));
    }

    /// <summary>Deletes SQLite projections and rebuilds from authoritative files.</summary>
    public void RebuildProjections()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _projections.RebuildFromStores(_cases, _operations);
            foreach (var op in _operations.ListAll())
            {
                if (op.ApprovalId is not null && op.Status is OperationStatus.Approved or OperationStatus.AwaitingApproval
                    or OperationStatus.Completed or OperationStatus.Denied)
                {
                    _projections.UpsertApproval(
                        op.ApprovalId,
                        op.OperationId,
                        op.CaseId,
                        op.CanonicalHashValue ?? op.CanonicalHash(),
                        _clock.UtcNow,
                        op.Status == OperationStatus.Denied ? "denied" : "approved");
                }
            }

            foreach (var cmd in _commands.ListPendingOrClaimed())
            {
                var record = _cases.TryLoadRecord(cmd.CaseId);
                if (record is null) continue;
                if (!record.PendingCommandIds.Contains(cmd.CommandId))
                {
                    record.PendingCommandIds.Add(cmd.CommandId);
                    PersistRecord(record);
                }
                if (record.Status == CaseStatus.Active)
                    _ready.TryEnqueue(record.Id, ReadyPriority.ToolOrDelegateCompletion);
                else if (record.Status == CaseStatus.Waiting && HasAwaiting(record))
                {
                    // approvals reconstructed via operations above
                }
            }
        }
    }

    internal async Task DispatchCommandIdsAsync(IReadOnlyList<string> commandIds, CancellationToken cancellationToken)
    {
        foreach (var id in commandIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip dispatch when case cancelled before claim — but still allow audit/feed/complete.
            RuntimeCommand? peek;
            lock (_gate)
            {
                peek = _commands.TryLoad(id);
                if (peek is null) continue;
                var record = _cases.TryLoadRecord(peek.CaseId);
                if (record is { Status: CaseStatus.Cancelled or CaseStatus.Completed }
                    && peek.Status == RuntimeCommandStatus.Pending
                    && IsEffectfulDispatch(peek.Kind))
                {
                    peek.Status = RuntimeCommandStatus.Cancelled;
                    peek.Error = "case_terminal_before_dispatch";
                    _commands.Save(peek);
                    record.PendingCommandIds.Remove(id);
                    PersistRecord(record);
                    continue;
                }
            }

            await _dispatcher.TryDispatchAsync(id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsEffectfulDispatch(string kind) => kind is
        RuntimeCommandKinds.ProposeOperation
        or RuntimeCommandKinds.DispatchAuthorizedOperation
        or RuntimeCommandKinds.RequestJudgments
        or RuntimeCommandKinds.RequestLocalJob
        or RuntimeCommandKinds.RequestReasoningJob
        or RuntimeCommandKinds.RetrieveContext
        or RuntimeCommandKinds.CreateOrLinkCase;

    private async Task<CommandDispatchResult> ExecutePersistedCommandAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        // Objective revision: drop stale commands.
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId);
            if (record is not null && command.ObjectiveRevision < record.ObjectiveRevision
                && command.Kind is not RuntimeCommandKinds.PublishFeedItem)
            {
                return new CommandDispatchResult(false, Error: "objective_revision_stale", Cancelled: true);
            }
            if (record is { Status: CaseStatus.Cancelled or CaseStatus.Completed }
                && command.Kind is RuntimeCommandKinds.DispatchAuthorizedOperation
                    or RuntimeCommandKinds.RequestJudgments
                    or RuntimeCommandKinds.RequestLocalJob
                    or RuntimeCommandKinds.RequestReasoningJob
                    or RuntimeCommandKinds.ProposeOperation)
            {
                return new CommandDispatchResult(false, Error: "case_terminal", Cancelled: true);
            }
        }

        return command.Kind switch
        {
            RuntimeCommandKinds.ProposeOperation => await Task.FromResult(DispatchProposeOperation(command)).ConfigureAwait(false),
            RuntimeCommandKinds.DispatchAuthorizedOperation => await Task.FromResult(DispatchAuthorized(command)).ConfigureAwait(false),
            RuntimeCommandKinds.RequestLocalJob => await DispatchLocalJobAsync(command, cancellationToken).ConfigureAwait(false),
            RuntimeCommandKinds.RequestJudgments => await DispatchJudgmentsAsync(command, cancellationToken).ConfigureAwait(false),
            RuntimeCommandKinds.RetrieveContext => await Task.FromResult(DispatchNoopComplete(command)).ConfigureAwait(false),
            RuntimeCommandKinds.RequestReasoningJob => await DispatchLocalJobAsync(command, cancellationToken).ConfigureAwait(false),
            RuntimeCommandKinds.CreateOrLinkCase => await Task.FromResult(DispatchCreateOrLink(command)).ConfigureAwait(false),
            RuntimeCommandKinds.ScheduleWake => await Task.FromResult(DispatchScheduleWake(command)).ConfigureAwait(false),
            RuntimeCommandKinds.PublishFeedItem => await Task.FromResult(DispatchPublishFeed(command)).ConfigureAwait(false),
            RuntimeCommandKinds.CompleteCase => await Task.FromResult(DispatchCompleteCase(command)).ConfigureAwait(false),
            _ => new CommandDispatchResult(false, Error: $"Unknown command kind '{command.Kind}'."),
        };
    }

    private CommandDispatchResult DispatchProposeOperation(RuntimeCommand command)
    {
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId)
                ?? throw new InvalidOperationException("Case missing for ProposeOperation.");
            var move = new CaseMove
            {
                Type = CaseMove.Propose,
                Name = PayloadString(command, "moveName") ?? PayloadString(command, "capability") ?? "unknown",
                Text = PayloadString(command, "label") ?? "",
                Args = new Dictionary<string, JsonElement>(command.Payload, StringComparer.Ordinal),
            };
            ProposeOperation(record, move, command.CausedByEventId ?? command.CommandId);
            ApplySegmentHandled(record, command);
            record.PendingCommandIds.Remove(command.CommandId);
            PersistRecord(record);
            return new CommandDispatchResult(true);
        }
    }

    private CommandDispatchResult DispatchAuthorized(RuntimeCommand command)
    {
        var operationId = command.OperationId ?? PayloadString(command, "operationId");
        if (operationId is null)
            return new CommandDispatchResult(false, Error: "missing operationId");
        try
        {
            DispatchOperation(operationId);
            lock (_gate)
            {
                var record = _cases.TryLoadRecord(command.CaseId);
                if (record is not null)
                {
                    record.PendingCommandIds.Remove(command.CommandId);
                    PersistRecord(record);
                }
            }
            return new CommandDispatchResult(true);
        }
        catch (Exception ex)
        {
            return new CommandDispatchResult(false, Error: ex.Message);
        }
    }

    private async Task<CommandDispatchResult> DispatchLocalJobAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        await _inference.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                var record = _cases.TryLoadRecord(command.CaseId)
                    ?? throw new InvalidOperationException("Case missing for local job.");
                var jobType = PayloadString(command, "jobType") ?? CaseMove.UseTool;
                var move = new CaseMove
                {
                    Type = jobType,
                    Name = PayloadString(command, "name") ?? "",
                    Text = PayloadString(command, "text") ?? "",
                    Args = new Dictionary<string, JsonElement>(command.Payload, StringComparer.Ordinal),
                };
                switch (jobType)
                {
                    case CaseMove.Build:
                        BeginToolBuild(record, move, command.CausedByEventId ?? command.CommandId);
                        break;
                    case CaseMove.RunWorkflow:
                        ExecuteWorkflow(record, move, command.CausedByEventId ?? command.CommandId);
                        break;
                    default:
                        ExecuteTool(record, move, command.CausedByEventId ?? command.CommandId);
                        break;
                }
                ApplySegmentHandled(record, command);
                record.PendingCommandIds.Remove(command.CommandId);
                if (record.Status is not CaseStatus.Waiting and not CaseStatus.Completed and not CaseStatus.Cancelled)
                    record.Status = CaseStatus.Active;
                PersistRecord(record);
            }
            return new CommandDispatchResult(true);
        }
        finally
        {
            _inference.Release();
        }
    }

    private async Task<CommandDispatchResult> DispatchJudgmentsAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        // Hook for slow/fake Jev: payload may include a wait handle name used only in tests via callback.
        if (command.Payload.TryGetValue("delayBarrier", out _) || _jevStallHook is not null)
        {
            if (_jevStallHook is not null)
                await _jevStallHook(command, cancellationToken).ConfigureAwait(false);
        }

        // Hosted judgment path: never silently replace Jev with local judgment.
        // When hosted is disabled or outbound blocks, record the block — local jobs may still run separately.
        if (command.Payload.TryGetValue("artifactIds", out var arts) && arts.ValueKind == JsonValueKind.Array)
        {
            var items = arts.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => new OutboundContentItem
                {
                    ArtifactId = e.GetString()!,
                    Role = "state",
                    Required = true,
                })
                .ToList();
            if (items.Count > 0)
            {
                var grantId = PayloadString(command, "grantId");
                var prepared = _hosted.Outbound.PrepareAndDispatch(
                    provider: PayloadString(command, "provider") ?? "jev",
                    purpose: HostedPurposes.Judgment,
                    items: items,
                    grantId: grantId);
                if (!prepared.Ok)
                {
                    lock (_gate)
                    {
                        var record = _cases.TryLoadRecord(command.CaseId);
                        if (record is null) return new CommandDispatchResult(false, Error: "case missing", Cancelled: true);
                        AppendEvent(record, CaseDomainEventTypes.CommandFailed, new
                        {
                            commandId = command.CommandId,
                            kind = command.Kind,
                            reason = prepared.BlockReason,
                            // Explicit: do not masquerade as a complete judgment.
                            judgmentComplete = false,
                        });
                        record.PendingCommandIds.Remove(command.CommandId);
                        PersistRecord(record);
                    }
                    return new CommandDispatchResult(false, Error: prepared.BlockReason, Cancelled: true);
                }
            }
        }

        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId);
            if (record is null) return new CommandDispatchResult(false, Error: "case missing");
            AppendEvent(record, CaseDomainEventTypes.CommandCompleted, new
            {
                commandId = command.CommandId,
                kind = command.Kind,
            });
            record.PendingCommandIds.Remove(command.CommandId);
            PersistRecord(record);
        }
        return new CommandDispatchResult(true);
    }

    private Func<RuntimeCommand, CancellationToken, Task>? _jevStallHook;
    private Func<RuntimeCommand, CancellationToken, Task>? _researchStallHook;

    /// <summary>Test hook: invoked while dispatching RequestJudgments (fake slow Jev).</summary>
    public void SetJudgmentStallHook(Func<RuntimeCommand, CancellationToken, Task>? hook) => _jevStallHook = hook;

    /// <summary>Test hook: invoked while applying research ops (fake slow research).</summary>
    public void SetResearchStallHook(Func<RuntimeCommand, CancellationToken, Task>? hook) => _researchStallHook = hook;

    private CommandDispatchResult DispatchCreateOrLink(RuntimeCommand command)
    {
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId)
                ?? throw new InvalidOperationException("Case missing.");
            var move = new CaseMove
            {
                Type = CaseMove.RaiseTask,
                Text = PayloadString(command, "objective") ?? PayloadString(command, "text") ?? "",
                Args = new Dictionary<string, JsonElement>(command.Payload, StringComparer.Ordinal),
            };
            RaiseChildTask(record, move, command.CausedByEventId ?? command.CommandId);
            ApplySegmentHandled(record, command);
            record.PendingCommandIds.Remove(command.CommandId);
            PersistRecord(record);
            return new CommandDispatchResult(true);
        }
    }

    private CommandDispatchResult DispatchScheduleWake(RuntimeCommand command)
    {
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId);
            if (record is null) return new CommandDispatchResult(false, Error: "case missing");
            if (record.Status is not CaseStatus.Cancelled and not CaseStatus.Completed)
            {
                record.Status = CaseStatus.Active;
                _ready.TryEnqueue(record.Id, ReadyPriority.Maintenance);
            }
            record.PendingCommandIds.Remove(command.CommandId);
            PersistRecord(record);
            return new CommandDispatchResult(true);
        }
    }

    private CommandDispatchResult DispatchPublishFeed(RuntimeCommand command)
    {
        lock (_gate)
        {
            var text = PayloadString(command, "text") ?? "";
            var level = PayloadString(command, "level") ?? "ambient";
            Feed(command.CaseId, text, level);
            var record = _cases.TryLoadRecord(command.CaseId);
            if (record is not null)
            {
                record.PendingCommandIds.Remove(command.CommandId);
                PersistRecord(record);
            }
            return new CommandDispatchResult(true);
        }
    }

    private CommandDispatchResult DispatchCompleteCase(RuntimeCommand command)
    {
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(command.CaseId)
                ?? throw new InvalidOperationException("Case missing.");
            var result = PayloadString(command, "result") ?? "done";
            record.Status = CaseStatus.Completed;
            record.Result = result;
            record.WaitingReason = null;
            AppendEvent(record, CaseEventTypes.CaseCompleted, new
            {
                result,
                sourceRefs = record.SourceRefs,
            });
            record.PendingCommandIds.Remove(command.CommandId);
            PersistRecord(record);
            _ready.Complete(record.Id);
            return new CommandDispatchResult(true);
        }
    }

    private static CommandDispatchResult DispatchNoopComplete(RuntimeCommand command)
        => new(true);

    private void ApplySegmentHandled(CaseRecord record, RuntimeCommand command)
    {
        if (record.Origin != CaseOrigin.Observed) return;
        var state = _intake.LoadState();
        if (state.CaseId != record.Id) return;
        var segId = PayloadString(command, "segmentId");
        // Explicit coverage required — no first-pending fallback (§9).
        if (segId is null) return;
        state.PendingMindSegmentIds.Remove(segId);

        var windowId = PayloadString(command, "windowId");
        if (windowId is not null)
        {
            _listening.MarkCovered(windowId, [segId]);
            var window = _listeningWindows.TryLoad(windowId);
            if (window?.Status == ListeningWindowStatus.Completed)
                state.PendingWindowIds.Remove(windowId);
        }

        _intake.SaveState(state);
    }

    private void ApplySegmentHandledFromEvent(CaseRecord record, CaseDomainEvent domainEvent)
    {
        if (record.Origin != CaseOrigin.Observed) return;
        var state = _intake.LoadState();
        if (state.CaseId != record.Id) return;
        string? segId = null;
        string? windowId = null;
        if (domainEvent.Payload.ValueKind == JsonValueKind.Object)
        {
            if (domainEvent.Payload.TryGetProperty("segmentId", out var seg)
                && seg.ValueKind == JsonValueKind.String)
                segId = seg.GetString();
            if (domainEvent.Payload.TryGetProperty("windowId", out var win)
                && win.ValueKind == JsonValueKind.String)
                windowId = win.GetString();
        }
        // Explicit coverage required — no first-pending fallback (§9).
        if (segId is null) return;
        state.PendingMindSegmentIds.Remove(segId);
        if (windowId is not null)
        {
            _listening.MarkCovered(windowId, [segId]);
            var window = _listeningWindows.TryLoad(windowId);
            if (window?.Status == ListeningWindowStatus.Completed)
                state.PendingWindowIds.Remove(windowId);
        }
        _intake.SaveState(state);
    }

    private OperationApplyResult ApplyOperationIdempotent(OperationEnvelope envelope)
    {
        // Crash after local write before ack: reuse stable operation-derived object id.
        var stableId = "op-result:" + envelope.OperationId;
        var existingMeta = Path.Combine(_objects.ObjectsDirectory, "by-id", stableId + ".json");
        if (File.Exists(existingMeta))
        {
            return new OperationApplyResult(true, "Idempotent resume; write already present.", stableId);
        }

        if (_researchStallHook is not null
            && envelope.Capability is ResearchCapabilities.Search or ResearchCapabilities.Delegate)
        {
            _researchStallHook(new RuntimeCommand
            {
                CommandId = "stall:" + envelope.OperationId,
                CaseId = envelope.CaseId,
                Kind = RuntimeCommandKinds.DispatchAuthorizedOperation,
                CreatedAt = _clock.UtcNow,
                OperationId = envelope.OperationId,
            }, CancellationToken.None).GetAwaiter().GetResult();
        }

        var applied = _broker.Apply(envelope);
        if (applied.Ok && applied.ResultRef is null)
        {
            var stored = _objects.PutJson(new { ok = true, operationId = envelope.OperationId, summary = applied.Summary }, stableId);
            return applied with { ResultRef = stored.ObjectId };
        }
        if (applied.Ok && applied.ResultRef is not null)
        {
            // Also index under stable id for resume.
            try
            {
                var text = CaseTools.ReadArtifactResult(_objects, applied.ResultRef);
                var body = text.GetType().GetProperty("body")?.GetValue(text) as string;
                if (body is not null)
                    _objects.PutText(body, stableId, "application/json");
            }
            catch { /* best effort */ }
        }
        return applied;
    }

    private CaseSnapshot BuildSnapshot(CaseRecord record)
    {
        var events = _cases.LoadEvents(record.Id);
        var pendingOps = record.PendingOperationIds
            .Select(id => _operations.TryLoad(id))
            .Where(op => op is not null)
            .Cast<OperationEnvelope>()
            .ToList();
        var segments = record.Origin == CaseOrigin.Observed
            ? FilterPendingSegments(record.Id)
            : (IReadOnlyList<ListeningSegmentView>)[];
        var availableTools = _tools?.Tools.Descriptors().Select(d => d.Name).ToList()
            ?? new List<string>();

        return new CaseSnapshot
        {
            CaseId = record.Id,
            Version = record.Version,
            SchemaVersion = record.SchemaVersion,
            Origin = record.Origin,
            Kind = record.Kind,
            Purpose = record.Purpose,
            AuthorizationStatus = record.AuthorizationStatus,
            ObjectiveRevision = record.ObjectiveRevision,
            ApprovedObjective = record.ApprovedObjective,
            Status = record.Status,
            Stage = record.Stage,
            WaitingReason = record.WaitingReason,
            ControllerId = record.ControllerId,
            ControllerVersion = record.ControllerVersion,
            PendingCommandIds = record.PendingCommandIds.ToList(),
            PendingOperationIds = record.PendingOperationIds.ToList(),
            PendingWaits = record.PendingWaits.ToList(),
            ProcessedEventIds = record.ProcessedEventIds.ToList(),
            DecisionDependencyRefs = record.DecisionDependencyRefs.ToList(),
            CompletionCriteria = record.CompletionCriteria,
            UnresolvedConflictIds = record.UnresolvedConflictIds.ToList(),
            SourceRefs = record.SourceRefs.ToList(),
            AllowedCapabilities = record.AllowedCapabilities.ToList(),
            Budgets = record.Budgets,
            ParentCaseId = record.ParentCaseId,
            ChildCaseIds = record.ChildCaseIds.ToList(),
            PresentationPolicy = record.PresentationPolicy,
            Result = record.Result,
            RecentEvents = events.TakeLast(64).ToList(),
            PendingOperations = pendingOps,
            RecentSegments = segments,
            AvailableTools = availableTools,
            At = _clock.UtcNow,
            StepsUsed = record.Budgets.StepsUsed,
        };
    }

    private IReadOnlyList<ListeningSegmentView> FilterPendingSegments(string caseId)
    {
        // Full backlog for durability; mind/controller only see uncovered/pending coverage.
        var all = _intake.LoadAllSegments(caseId);
        var state = _intake.LoadState();
        if (state.PendingMindSegmentIds.Count == 0) return [];
        var pending = new HashSet<string>(state.PendingMindSegmentIds, StringComparer.Ordinal);
        return all.Where(s => pending.Contains(s.SegmentId)).OrderBy(s => s.Sequence).ToList();
    }

    private static string? PayloadString(RuntimeCommand command, string key)
    {
        if (!command.Payload.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>Enqueues a raw persisted command (used by tests and recovery).</summary>
    public RuntimeCommand EnqueueCommand(RuntimeCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _commands.Save(command);
            var record = _cases.TryLoadRecord(command.CaseId)
                ?? throw new InvalidOperationException($"Case '{command.CaseId}' not found.");
            if (!record.PendingCommandIds.Contains(command.CommandId))
                record.PendingCommandIds.Add(command.CommandId);
            PersistRecord(record);
            return command;
        }
    }
}
