using System.Diagnostics;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Listening;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// The one decision loop for durable cases. Persists every case event before dispatching
/// consequences. Clean shutdown suspends; restart reconstructs from stores.
/// </summary>
public sealed partial class CaseRuntime : IDisposable
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly ICaseMind _mind;
    private readonly ICaseController _controller;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly CaseStore _cases;
    private readonly OperationStore _operations;
    private readonly CommandStore _commands;
    private readonly CommandDispatcher _dispatcher;
    private readonly HostedAuthorization _hosted;
    private readonly ObjectStore _objects;
    private readonly ProjectionDatabase _projections;
    private readonly ReadyQueue _ready;
    private readonly InferenceLease _inference;
    private readonly CaseLocalContext _local;
    private readonly OperationBroker _broker;
    private readonly StreamIntake _intake;
    private readonly ListeningWindowStore _listeningWindows;
    private readonly ListeningController _listening;
    private readonly ResearchBroker? _research;
    private readonly ResearchServices _researchServices;
    private readonly ToolWorkflowBroker? _tools;
    private readonly ToolServices _toolServices;
    private readonly Action _onSideEffect;
    private readonly string _leaseOwner;
    private readonly object _gate = new();
    private bool _disposed;

    private CaseRuntime(
        DataRoot root,
        IClock clock,
        ICaseMind mind,
        ICaseController controller,
        RuntimeDiagnostics diagnostics,
        Action? onSideEffect,
        CaseLocalContext? local,
        ResearchServices? research,
        ToolServices? tools,
        RuntimeConcurrencyOptions? concurrency,
        HostedAuthorization? hosted)
    {
        _root = root;
        _clock = clock;
        _mind = mind;
        _controller = controller;
        _diagnostics = diagnostics;
        _onSideEffect = onSideEffect ?? (() => { });
        _researchServices = research ?? new ResearchServices();
        _toolServices = tools ?? new ToolServices();
        _cases = new CaseStore(root);
        _operations = new OperationStore(root);
        _commands = new CommandStore(root);
        _objects = new ObjectStore(root, clock);
        _projections = ProjectionDatabase.Open(root);
        _ready = new ReadyQueue(_projections, clock);
        _inference = new InferenceLease();
        _local = local ?? new CaseLocalContext(root, clock);
        _broker = new OperationBroker(_local, _objects, clock, _onSideEffect);
        _intake = new StreamIntake(root, _objects, _cases, clock);
        _listeningWindows = new ListeningWindowStore(root, clock);
        _listening = new ListeningController(_listeningWindows, clock);
        _research = _researchServices.Search is not null || _researchServices.Delegate is not null
            ? new ResearchBroker(root, _objects, clock, _researchServices)
            : null;
        _tools = _toolServices.Runner is not null || _toolServices.Drafter is not null
            ? new ToolWorkflowBroker(root, _objects, _toolServices, () => clock.UtcNow)
            : null;
        _leaseOwner = Ulid.NewUlid(clock.UtcNow);
        _hosted = hosted ?? new HostedAuthorization(root, clock, hostedEnabled: false);
        _dispatcher = new CommandDispatcher(
            _commands,
            concurrency ?? new RuntimeConcurrencyOptions(),
            _leaseOwner,
            ExecutePersistedCommandAsync);
    }

    public CaseStore Cases => _cases;
    public OperationStore Operations => _operations;
    public CommandStore Commands => _commands;
    public CommandDispatcher Dispatcher => _dispatcher;
    public ICaseController Controller => _controller;
    public InferenceLease Inference => _inference;
    public ObjectStore Objects => _objects;
    public ReadyQueue Ready => _ready;
    public ProjectionDatabase Projections => _projections;
    public CaseLocalContext Local => _local;
    public StreamIntake Intake => _intake;
    public ListeningController Listening => _listening;
    public ListeningWindowStore ListeningWindows => _listeningWindows;
    public ResearchBroker? Research => _research;
    public ToolWorkflowBroker? Tools => _tools;
    public HostedAuthorization Hosted => _hosted;
    public bool SearchAvailable => _research?.SearchAvailable == true;
    public bool ToolBuildAvailable => _tools?.CanBuild == true;
    public int SideEffectCount { get; private set; }

    /// <summary>Opens a runtime on an existing data root, reconstructing suspended work.</summary>
    public static CaseRuntime Open(
        DataRoot root,
        IClock clock,
        ICaseMind mind,
        RuntimeDiagnostics diagnostics,
        Action? onSideEffect = null,
        CaseLocalContext? local = null,
        ResearchServices? research = null,
        ToolServices? tools = null,
        ICaseController? controller = null,
        RuntimeConcurrencyOptions? concurrency = null,
        HostedAuthorization? hosted = null)
    {
        root.EnsureLayout(clock);
        var resolvedController = controller ?? new LegacyMindBridgeController(mind);
        var runtime = new CaseRuntime(root, clock, mind, resolvedController, diagnostics, onSideEffect, local, research, tools, concurrency, hosted);
        runtime.Reconstruct();
        diagnostics.Write(clock.UtcNow, "info", "CaseRuntime", "opened",
            status: research?.SearchAvailable == true
                ? "ok_search_bound"
                : tools?.CanBuild == true ? "ok_tools_bound" : "ok");
        return runtime;
    }

    private void Reconstruct()
    {
        _local.RebuildIndex();
        foreach (var caseId in _cases.ListCaseIds())
        {
            var recovery = _cases.LoadEventsWithRecovery(caseId);
            if (recovery.TruncatedTailRecovered || recovery.MidFileCorruptionStopped)
            {
                _diagnostics.Write(_clock.UtcNow, "warn", "CaseRuntime",
                    recovery.MidFileCorruptionStopped ? "jsonl_mid_corruption" : "jsonl_truncated_tail",
                    caseId: caseId, status: recovery.IncidentPath);
            }

            var record = _cases.TryLoadRecord(caseId);
            if (record is null) continue;
            _projections.UpsertCase(record);

            if (record.Status == CaseStatus.Suspended)
            {
                var hasAwaiting = HasAwaiting(record);

                if (hasAwaiting)
                {
                    record.Status = CaseStatus.Waiting;
                    record.UpdatedAt = _clock.UtcNow;
                    PersistRecord(record);
                    _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "resumed_waiting",
                        caseId: record.Id, caseVersion: record.Version, status: record.Status);
                }
                else
                {
                    record.Status = CaseStatus.Active;
                    record.UpdatedAt = _clock.UtcNow;
                    PersistRecord(record);
                    var priority = record.Origin == CaseOrigin.Observed
                        ? ReadyPriority.NormalObserved
                        : ReadyPriority.UserReplyOrApproval;
                    _ready.TryEnqueue(record.Id, priority);
                    AppendEvent(record, CaseEventTypes.CaseResumed, new { reason = "restart" });
                    _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "resumed_ready",
                        caseId: record.Id, caseVersion: record.Version, status: record.Status);
                }
            }
        }

        foreach (var op in _operations.ListAll())
            _projections.UpsertOperation(op);

        // Resume pending/claimed outbox commands once (crash after transition before dispatch).
        foreach (var cmd in _commands.ListPendingOrClaimed())
        {
            if (cmd.Status == RuntimeCommandStatus.Claimed)
            {
                // Previous owner died; return to pending for a single resume.
                cmd.Status = RuntimeCommandStatus.Pending;
                cmd.ClaimedBy = null;
                cmd.ClaimedAt = null;
                _commands.Save(cmd);
            }

            var record = _cases.TryLoadRecord(cmd.CaseId);
            if (record is null) continue;
            if (record.Status is CaseStatus.Cancelled or CaseStatus.Completed)
            {
                // Never dispatch not-yet-started work after cancel/complete.
                if (cmd.Status == RuntimeCommandStatus.Pending)
                {
                    cmd.Status = RuntimeCommandStatus.Cancelled;
                    cmd.Error = "case_terminal";
                    _commands.Save(cmd);
                }
                continue;
            }

            if (!record.PendingCommandIds.Contains(cmd.CommandId))
                record.PendingCommandIds.Add(cmd.CommandId);
            PersistRecord(record);
            if (record.Status == CaseStatus.Active)
                _ready.TryEnqueue(record.Id, ReadyPriority.ToolOrDelegateCompletion);
        }

        var listening = _intake.LoadState();
        if (listening.Active && listening.CaseId is { } listenId)
        {
            var listenCase = _cases.TryLoadRecord(listenId);
            if (listenCase is not null
                && listenCase.Status is not CaseStatus.Completed and not CaseStatus.Cancelled)
            {
                if (listening.PendingMindSegmentIds.Count > 0 && listenCase.Status == CaseStatus.Active)
                    _ready.TryEnqueue(listenId, ReadyPriority.NormalObserved);

                _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "listening_resumed",
                    caseId: listenId, status: listenCase.Status);
            }
        }
    }

    private bool HasAwaiting(CaseRecord record)
        => record.PendingOperationIds.Select(id => _operations.TryLoad(id))
            .Any(op => op is { Status: OperationStatus.AwaitingApproval });

    /// <summary>Opens (or returns) the long-lived observed listening case.</summary>
    public CaseRecord StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var state = _intake.LoadState();
            if (state.Active && state.CaseId is { } existingId)
            {
                var existing = _cases.TryLoadRecord(existingId);
                if (existing is not null && existing.Status is not CaseStatus.Completed and not CaseStatus.Cancelled)
                    return Clone(existing);
            }

            var now = _clock.UtcNow;
            var id = Ulid.NewUlid(now);
            var record = new CaseRecord
            {
                Id = id,
                Version = 0,
                Origin = CaseOrigin.Observed,
                Kind = CaseKind.Check,
                ApprovedObjective = "Listen to the enabled conversation stream.",
                Status = CaseStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                PresentationPolicy = StreamIntake.PresentationListening,
                AllowedCapabilities =
                [
                    CaseTools.LocalSearch,
                    CaseTools.ReadNote,
                    Actions.ModifyNote,
                    Actions.CreateDraftNote,
                    "file_note",
                    CaseMove.RaiseTask,
                ],
            };
            PersistRecord(record);
            var evt = AppendEvent(record, CaseEventTypes.ListeningStarted, new { at = now });
            record.ProcessedEventIds.Add(evt.EventId);
            PersistRecord(record);

            state = new ListeningSessionState
            {
                CaseId = id,
                SessionId = id,
                Active = true,
                CaptureEnabled = true,
                RetentionPolicy = "transcript_30d",
                UpdatedAt = now,
            };
            _intake.SaveState(state);
            Feed(id, "Listening started.", "ambient");
            _diagnostics.Write(now, "info", "CaseRuntime", "listening_started", caseId: id, status: record.Status);
            return Clone(record);
        }
    }

    /// <summary>
    /// Persists a transcript segment to the object store, then appends a case event and marks it ingested.
    /// </summary>
    public ListeningSegmentView IngestSegment(string text, DateTimeOffset? ts = null, string? speaker = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var listening = StartListeningUnlocked();
            var at = ts ?? _clock.UtcNow;
            var (segmentId, sequence, stored, payload) = _intake.PrepareSegment(text, at, speaker);

            var evt = AppendEvent(listening, CaseEventTypes.SegmentIngested, payload);
            listening.ProcessedEventIds.Add(evt.EventId);
            // Listening case stays active; new talk becomes ready work at observed priority.
            if (listening.Status is CaseStatus.Waiting)
            {
                // Still waiting on approval — do not steal focus; segment is persisted for later.
            }
            else
            {
                listening.Status = CaseStatus.Active;
            }
            PersistRecord(listening);

            var state = _intake.LoadState();
            state.CaseId = listening.Id;
            state.SessionId ??= listening.Id;
            state.Active = true;
            state.CaptureEnabled = true;
            if (!state.IngestedSegmentIds.Contains(segmentId))
                state.IngestedSegmentIds.Add(segmentId);
            if (!state.PendingMindSegmentIds.Contains(segmentId))
                state.PendingMindSegmentIds.Add(segmentId);

            // Durable windows: form/extend coverage from full backlog (not recent-32 only).
            var sessionId = state.SessionId ?? listening.Id;
            var sequenced = _intake.ToSequenced(listening.Id);
            var covered = _listening.CoveredPrimarySegmentIds(sessionId);
            var windows = _listening.FormWindows(sessionId, listening.Id, sequenced, covered);
            foreach (var w in windows)
            {
                if (!state.PendingWindowIds.Contains(w.WindowId))
                    state.PendingWindowIds.Add(w.WindowId);
            }
            _intake.SaveState(state);

            if (listening.Status == CaseStatus.Active)
                _ready.TryEnqueue(listening.Id, ReadyPriority.NormalObserved);

            // Transcript persists locally; default restriction is local_only (hosted_eligible ≠ permission).
            _hosted.Evidence.PutText(
                text,
                sourceRefs: [$"segment:{segmentId}", $"object:{stored.ObjectId}"],
                sessionIds: [listening.Id],
                restriction: Relay.Core.Evidence.ContentRestriction.LocalOnly,
                label: "transcript_segment",
                artifactId: "seg:" + segmentId,
                kind: "transcript");

            _diagnostics.Write(at, "info", "CaseRuntime", "segment_ingested",
                caseId: listening.Id, caseVersion: listening.Version, resultRef: stored.ObjectId);
            return new ListeningSegmentView(segmentId, evt.EventId, stored.ObjectId, stored.Sha256, at, speaker, text, sequence);
        }
    }

    private CaseRecord StartListeningUnlocked()
    {
        var state = _intake.LoadState();
        if (state.Active && state.CaseId is { } existingId)
        {
            var existing = _cases.TryLoadRecord(existingId);
            if (existing is not null && existing.Status is not CaseStatus.Completed and not CaseStatus.Cancelled)
                return existing;
        }

        // Nested create without re-entering StartListening lock.
        var now = _clock.UtcNow;
        var id = Ulid.NewUlid(now);
        var record = new CaseRecord
        {
            Id = id,
            Version = 0,
            Origin = CaseOrigin.Observed,
            Kind = CaseKind.Check,
            ApprovedObjective = "Listen to the enabled conversation stream.",
            Status = CaseStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            PresentationPolicy = StreamIntake.PresentationListening,
            AllowedCapabilities =
            [
                CaseTools.LocalSearch,
                CaseTools.ReadNote,
                Actions.ModifyNote,
                Actions.CreateDraftNote,
                "file_note",
                CaseMove.RaiseTask,
            ],
        };
        PersistRecord(record);
        var evt = AppendEvent(record, CaseEventTypes.ListeningStarted, new { at = now });
        record.ProcessedEventIds.Add(evt.EventId);
        PersistRecord(record);
        _intake.SaveState(new ListeningSessionState
        {
            CaseId = id,
            SessionId = id,
            Active = true,
            CaptureEnabled = true,
            RetentionPolicy = "transcript_30d",
            UpdatedAt = now,
        });
        return record;
    }

    public void StopListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var state = _intake.LoadState();
            if (state.CaseId is { } id)
            {
                var record = _cases.TryLoadRecord(id);
                if (record is not null && record.Status is not CaseStatus.Completed and not CaseStatus.Cancelled)
                {
                    AppendEvent(record, CaseEventTypes.ListeningStopped, new { at = _clock.UtcNow });
                    // Listening case remains for history; mark completed when explicitly stopped.
                    record.Status = CaseStatus.Completed;
                    record.Result = "listening stopped";
                    PersistRecord(record);
                }
            }
            state.Active = false;
            state.PendingMindSegmentIds.Clear();
            _intake.SaveState(state);
        }
    }

    public CaseRecord? GetListeningCase()
    {
        var state = _intake.LoadState();
        if (!state.Active || state.CaseId is null) return null;
        var record = _cases.TryLoadRecord(state.CaseId);
        if (record is null) return null;
        if (record.Status is CaseStatus.Completed or CaseStatus.Cancelled) return null;
        return record;
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
                AllowedCapabilities =
                [
                    ScriptedCaseMind.DefaultCapability,
                    Actions.CreateProject,
                    Actions.ModifyNote,
                    Actions.CreateDraftNote,
                    "file_note",
                    CaseTools.LocalSearch,
                    CaseTools.ReadNote,
                    CaseTools.ReadArtifact,
                    ResearchCapabilities.Search,
                    ResearchCapabilities.Delegate,
                    Actions.ModelRequest,
                    ToolCapabilities.PromoteTool,
                    ToolCapabilities.RevertTool,
                    ToolCapabilities.PromoteWorkflow,
                    ToolCapabilities.RevertWorkflow,
                    CaseMove.Build,
                    CaseMove.RunWorkflow,
                ],
            };

            // Do not advertise search unless a real adapter is bound.
            if (!SearchAvailable)
                record.AllowedCapabilities.Remove(ResearchCapabilities.Search);
            if (!ToolBuildAvailable)
            {
                record.AllowedCapabilities.Remove(ToolCapabilities.PromoteTool);
                record.AllowedCapabilities.Remove(CaseMove.Build);
            }

            PersistRecord(record);
            var evt = AppendEvent(record, CaseEventTypes.UserInput, new { text = objective, origin = CaseOrigin.Direct });
            record.ProcessedEventIds.Add(evt.EventId);
            PersistRecord(record);

            _ready.TryEnqueue(record.Id, ReadyPriority.NewDirectRequest);
            Feed(record.Id, $"New direct ask: {Clip(objective, 120)}", "persistent");
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
            // Drop the lease, then re-queue if the case is still runnable (e.g. after a tool).
            _ready.Complete(lease.CaseId);
            var record = _cases.TryLoadRecord(lease.CaseId);
            if (record is { Status: CaseStatus.Active })
                _ready.TryEnqueue(lease.CaseId, ReadyPriority.ToolOrDelegateCompletion);
        }
    }

    /// <summary>Steps until the case is waiting, completed, cancelled, quiet (listening), or <paramref name="maxSteps"/> is hit.</summary>
    public async Task<CaseRecord> RunUntilIdleAsync(string caseId, int maxSteps = 16, CancellationToken cancellationToken = default)
    {
        CaseRecord? last = null;
        for (var i = 0; i < maxSteps; i++)
        {
            last = await StepCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
            if (last.Status is CaseStatus.Waiting or CaseStatus.Completed or CaseStatus.Cancelled or CaseStatus.Suspended)
                return last;

            if (last.Origin == CaseOrigin.Observed)
            {
                var state = _intake.LoadState();
                var awaiting = last.PendingOperationIds
                    .Select(id => _operations.TryLoad(id))
                    .Any(op => op is { Status: OperationStatus.AwaitingApproval });
                if (!awaiting && state.PendingMindSegmentIds.Count == 0)
                    return last;
            }
            // Tool path leaves Active; continue.
        }
        return last ?? throw new InvalidOperationException("Case not found.");
    }

    public async Task<CaseRecord> StepCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> toDispatch = [];

        // 1–6: short lock — load, validate, controller transition, persist outbox, update snapshot.
        // No model/network wait holds this lock.
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId)
                ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
            if (record.Status is CaseStatus.Completed or CaseStatus.Cancelled)
                return Clone(record);

            if (record.Status == CaseStatus.Waiting && HasAwaiting(record))
                return Clone(record);

            // Waiting cases: no controller/model calls until a relevant event or scheduled retry.
            // Manual StepCaseAsync counts as a scheduled/manual wake for historical harnesses.
            var input = new CaseInput
            {
                Kind = CaseInput.ManualStep,
                At = _clock.UtcNow,
            };

            var snapshot = BuildSnapshot(record);
            CaseTransition transition;
            try
            {
                transition = _controller.Handle(snapshot, input);
            }
            catch (Exception ex)
            {
                _diagnostics.Write(_clock.UtcNow, "error", "CaseRuntime", "controller_failed",
                    caseId: record.Id, caseVersion: record.Version, error: ex.Message, latencyMs: sw.ElapsedMilliseconds);
                throw;
            }

            if (transition.Skip)
                return Clone(record);

            record.ControllerId ??= _controller.ControllerId;
            record.ControllerVersion ??= _controller.ControllerVersion;

            var persistedCommandIds = new List<string>();
            foreach (var domainEvent in transition.Events)
            {
                CaseReducer.ApplyToRecord(record, domainEvent);
                if (domainEvent.Type == CaseDomainEventTypes.SegmentHandled)
                    ApplySegmentHandledFromEvent(record, domainEvent);
                var evt = AppendEvent(record, MapDomainEventType(domainEvent.Type), domainEvent.Payload);
                record.ProcessedEventIds.Add(evt.EventId);
            }

            foreach (var command in transition.Commands)
            {
                // Deduplicate by stable command id (crash-safe identity).
                var existing = _commands.TryLoad(command.CommandId);
                if (existing is not null)
                {
                    if (!record.PendingCommandIds.Contains(existing.CommandId)
                        && existing.Status is RuntimeCommandStatus.Pending or RuntimeCommandStatus.Claimed)
                        record.PendingCommandIds.Add(existing.CommandId);
                    if (existing.Status is RuntimeCommandStatus.Pending or RuntimeCommandStatus.Claimed)
                        persistedCommandIds.Add(existing.CommandId);
                    continue;
                }

                command.Status = RuntimeCommandStatus.Pending;
                _commands.Save(command);
                if (!record.PendingCommandIds.Contains(command.CommandId))
                    record.PendingCommandIds.Add(command.CommandId);
                persistedCommandIds.Add(command.CommandId);
            }

            _cases.AppendTransition(record.Id, new
            {
                transitionId = Ulid.NewUlid(_clock.UtcNow),
                caseId = record.Id,
                caseVersion = record.Version,
                at = _clock.UtcNow,
                eventTypes = transition.Events.Select(e => e.Type).ToList(),
                commandIds = persistedCommandIds,
            });

            PersistRecord(record);
            toDispatch = persistedCommandIds;
            _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "case_stepped",
                caseId: record.Id, caseVersion: record.Version, status: record.Status, latencyMs: sw.ElapsedMilliseconds);
        }

        // 7: dispatch persisted commands outside the case lock.
        await DispatchCommandIdsAsync(toDispatch, cancellationToken).ConfigureAwait(false);

        // 8: persist completion side-effects already done per command; enqueue if still active.
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId) ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
            if (record.Status == CaseStatus.Active)
                _ready.TryEnqueue(record.Id, ReadyPriority.ToolOrDelegateCompletion);
            return Clone(record);
        }
    }

    private static string MapDomainEventType(string domainType) => domainType switch
    {
        CaseDomainEventTypes.MindStepped => CaseEventTypes.MindStepped,
        CaseDomainEventTypes.WaitEntered => CaseEventTypes.WaitEntered,
        CaseDomainEventTypes.CaseCompleted => CaseEventTypes.CaseCompleted,
        CaseDomainEventTypes.CaseCancelled => CaseEventTypes.CaseCancelled,
        CaseDomainEventTypes.MoveRejected => CaseEventTypes.MoveRejected,
        CaseDomainEventTypes.ToolCalled => CaseEventTypes.ToolCalled,
        CaseDomainEventTypes.ToolResult => CaseEventTypes.ToolResult,
        CaseDomainEventTypes.TaskRaised => CaseEventTypes.TaskRaised,
        CaseDomainEventTypes.OperationProposed => CaseEventTypes.OperationProposed,
        CaseDomainEventTypes.DuplicateIgnored => CaseEventTypes.DuplicateIgnored,
        _ => domainType,
    };

    private CaseEvent AppendEvent(CaseRecord record, string type, JsonElement payload)
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
            Payload = payload,
        };
        _cases.AppendEvent(evt);
        return evt;
    }

    private void ApplyMove(CaseRecord record, CaseMindStep step, string causedByEventId)
    {
        if (!IsMoveAllowed(record, step.Move, out var denyReason))
        {
            AppendEvent(record, CaseEventTypes.MoveRejected, new
            {
                move = step.Move.Type,
                name = step.Move.Name,
                reason = denyReason,
            });
            if (record.Origin == CaseOrigin.Observed)
                record.Status = CaseStatus.Active;
            return;
        }

        switch (step.Move.Type)
        {
            case CaseMove.UseTool:
                ExecuteTool(record, step.Move, causedByEventId);
                break;
            case CaseMove.Build:
                BeginToolBuild(record, step.Move, causedByEventId);
                break;
            case CaseMove.RunWorkflow:
                ExecuteWorkflow(record, step.Move, causedByEventId);
                break;
            case CaseMove.Propose:
                ProposeOperation(record, step.Move, causedByEventId);
                break;
            case CaseMove.RaiseTask:
                RaiseChildTask(record, step.Move, causedByEventId);
                break;
            case CaseMove.Wait:
                if (record.PresentationPolicy == StreamIntake.PresentationListening)
                {
                    record.Status = CaseStatus.Active;
                    if (!record.PendingWaits.Contains(step.Move.Text))
                        record.PendingWaits.Add(step.Move.Text);
                    AppendEvent(record, CaseEventTypes.WaitEntered, new { reason = step.Move.Text, keepListening = true });
                }
                else
                {
                    record.Status = CaseStatus.Waiting;
                    if (!record.PendingWaits.Contains(step.Move.Text))
                        record.PendingWaits.Add(step.Move.Text);
                    AppendEvent(record, CaseEventTypes.WaitEntered, new { reason = step.Move.Text });
                }
                break;
            case CaseMove.Stop:
                ApplyCitations(record, step.Move);
                record.Status = CaseStatus.Completed;
                record.Result = step.Move.Text;
                AppendEvent(record, CaseEventTypes.CaseCompleted, new
                {
                    result = step.Move.Text,
                    sourceRefs = record.SourceRefs,
                });
                break;
            case CaseMove.Say when step.Move.Done
                && record.PresentationPolicy != StreamIntake.PresentationListening:
                ApplyCitations(record, step.Move);
                record.Status = CaseStatus.Completed;
                record.Result = step.Move.Text;
                AppendEvent(record, CaseEventTypes.CaseCompleted, new
                {
                    result = step.Move.Text,
                    sourceRefs = record.SourceRefs,
                });
                break;
            case CaseMove.Say:
                ApplyCitations(record, step.Move);
                if (record.PresentationPolicy == StreamIntake.PresentationListening)
                    record.Status = CaseStatus.Active;
                break;
            default:
                break;
        }
    }

    private static bool IsMoveAllowed(CaseRecord record, CaseMove move, out string? reason)
    {
        reason = null;
        if (record.Origin != CaseOrigin.Observed) return true;

        switch (move.Type)
        {
            case CaseMove.Say:
            case CaseMove.Wait:
            case CaseMove.Stop:
            case CaseMove.RaiseTask:
                return true;
            case CaseMove.UseTool:
            {
                var tool = string.IsNullOrWhiteSpace(move.Name) ? CaseTools.ArgString(move.Args, "tool") : move.Name;
                if (tool is CaseTools.LocalSearch or CaseTools.ReadNote) return true;
                reason = $"Observed cases may only use read-only tools (got '{tool}').";
                return false;
            }
            case CaseMove.Propose:
            {
                var cap = move.Name;
                if (move.Args.TryGetValue("capability", out var c) && c.ValueKind == JsonValueKind.String)
                    cap = c.GetString() ?? cap;
                if (cap is Actions.ModifyNote or Actions.CreateDraftNote or "file_note") return true;
                reason = $"Observed cases may not propose '{cap}'.";
                return false;
            }
            default:
                reason = $"Move '{move.Type}' is not allowed on observed cases.";
                return false;
        }
    }

    private void RaiseChildTask(CaseRecord parent, CaseMove move, string causedByEventId)
    {
        var objective = move.Text;
        if (move.Args.TryGetValue("objective", out var objEl) && objEl.ValueKind == JsonValueKind.String)
            objective = objEl.GetString() ?? objective;
        if (string.IsNullOrWhiteSpace(objective))
            objective = "Raised from listening.";

        var kind = CaseKind.Remember;
        if (move.Args.TryGetValue("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
            kind = kindEl.GetString() ?? kind;

        var now = _clock.UtcNow;
        var childId = Ulid.NewUlid(now);
        var sourceRefs = new List<string>();
        if (move.Args.TryGetValue("segmentId", out var segEl) && segEl.ValueKind == JsonValueKind.String)
            sourceRefs.Add("segment:" + segEl.GetString());
        if (move.Args.TryGetValue("sourceEventId", out var evEl) && evEl.ValueKind == JsonValueKind.String)
            sourceRefs.Add("event:" + evEl.GetString());
        sourceRefs.Add("parent:" + parent.Id);

        var child = new CaseRecord
        {
            Id = childId,
            Version = 0,
            Origin = CaseOrigin.Direct,
            Kind = kind,
            ApprovedObjective = objective,
            Status = CaseStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            ParentCaseId = parent.Id,
            SourceRefs = sourceRefs,
            AllowedCapabilities =
            [
                ScriptedCaseMind.DefaultCapability,
                Actions.CreateProject,
                Actions.ModifyNote,
                Actions.CreateDraftNote,
                "file_note",
                CaseTools.LocalSearch,
                CaseTools.ReadNote,
            ],
        };
        PersistRecord(child);
        var userEvt = AppendEvent(child, CaseEventTypes.UserInput, new
        {
            text = objective,
            origin = CaseOrigin.Direct,
            raisedFrom = parent.Id,
            causedByEventId,
        });
        child.ProcessedEventIds.Add(userEvt.EventId);
        PersistRecord(child);
        _ready.TryEnqueue(childId, ReadyPriority.NewDirectRequest);

        if (!parent.ChildCaseIds.Contains(childId))
            parent.ChildCaseIds.Add(childId);
        var raised = AppendEvent(parent, CaseEventTypes.TaskRaised, new
        {
            childCaseId = childId,
            objective,
            causedByEventId,
            sourceRefs,
        });
        parent.ProcessedEventIds.Add(raised.EventId);
        parent.Status = CaseStatus.Active;
        Feed(childId, $"Raised: {Clip(objective, 120)}", "persistent");
        Feed(parent.Id, $"Raised task {childId}.", "persistent");
        _diagnostics.Write(now, "info", "CaseRuntime", "task_raised",
            caseId: parent.Id, caseVersion: parent.Version, status: parent.Status);
    }

    /// <summary>
    /// Marks segments handled only when explicit coverage (segmentId) is provided.
    /// The former first-pending fallback without explicit coverage is removed (§9).
    /// </summary>
    private void MarkListeningSegmentsHandled(CaseRecord record, CaseMove move)
    {
        if (record.Origin != CaseOrigin.Observed) return;
        var state = _intake.LoadState();
        if (state.CaseId != record.Id) return;

        if (move.Args.TryGetValue("segmentId", out var seg) && seg.ValueKind == JsonValueKind.String)
        {
            var id = seg.GetString();
            if (id is not null) state.PendingMindSegmentIds.Remove(id);
            _intake.SaveState(state);
        }
        // No fallback: do not mark the first pending segment without explicit coverage.
    }

    private static string ResolveAttention(CaseRecord record, CaseMove move, string feed)
    {
        if (move.Args.TryGetValue("attention", out var att) && att.ValueKind == JsonValueKind.String)
            return att.GetString() ?? "ambient";
        if (move.Type == CaseMove.Say && move.Done && record.PresentationPolicy != StreamIntake.PresentationListening)
            return "finding";
        if (move.Type == CaseMove.Propose) return "proposal";
        if (move.Type == CaseMove.RaiseTask) return "persistent";
        if (feed.Contains(" means ", StringComparison.OrdinalIgnoreCase)) return "persistent";
        return "ambient";
    }

    private static void ApplyCitations(CaseRecord record, CaseMove move)
    {
        if (!move.Args.TryGetValue("citations", out var cites) || cites.ValueKind != JsonValueKind.Array)
            return;
        foreach (var c in cites.EnumerateArray())
        {
            if (c.TryGetProperty("objectId", out var oid) && oid.ValueKind == JsonValueKind.String)
            {
                var refText = "artifact:" + oid.GetString();
                if (!record.SourceRefs.Contains(refText))
                    record.SourceRefs.Add(refText);
                continue;
            }
            var noteId = c.TryGetProperty("noteId", out var n) ? n.GetString() : null;
            var projectId = c.TryGetProperty("projectId", out var p) ? p.GetString() : null;
            var eventId = c.TryGetProperty("eventId", out var e) ? e.GetString() : null;
            var start = c.TryGetProperty("start", out var s) && s.TryGetInt32(out var si) ? si : 0;
            var end = c.TryGetProperty("end", out var en) && en.TryGetInt32(out var ei) ? ei : 0;
            var refText2 = $"note:{projectId}/{noteId}#{eventId}:{start}-{end}";
            if (!string.IsNullOrEmpty(noteId) && !record.SourceRefs.Contains(refText2))
                record.SourceRefs.Add(refText2);
        }
    }

    private void ExecuteTool(CaseRecord record, CaseMove move, string causedByEventId)
    {
        var tool = string.IsNullOrWhiteSpace(move.Name) ? CaseTools.ArgString(move.Args, "tool") : move.Name;
        AppendEvent(record, CaseEventTypes.ToolCalled, new { tool, args = move.Args, causedByEventId });

        object result;
        try
        {
            result = tool switch
            {
                CaseTools.LocalSearch => CaseTools.LocalSearchResult(
                    _local,
                    CaseTools.ArgString(move.Args, "query", record.ApprovedObjective ?? ""),
                    string.IsNullOrEmpty(CaseTools.ArgString(move.Args, "projectId")) ? null : CaseTools.ArgString(move.Args, "projectId")),
                CaseTools.ReadNote => CaseTools.ReadNoteResult(
                    _local,
                    CaseTools.ArgString(move.Args, "projectId"),
                    CaseTools.ArgString(move.Args, "noteId")),
                CaseTools.ReadArtifact => CaseTools.ReadArtifactResult(
                    _objects,
                    CaseTools.ArgString(move.Args, "objectId")),
                _ => RunPromotedTool(record, tool, move),
            };
        }
        catch (Exception ex)
        {
            var failEvt = AppendEvent(record, CaseEventTypes.ToolResult, new { tool, ok = false, error = ex.Message });
            record.ProcessedEventIds.Add(failEvt.EventId);
            Feed(record.Id, $"Tool {tool} failed.", "alert");
            record.Status = CaseStatus.Active;
            return;
        }

        var stored = _objects.PutJson(result);
        var evt = AppendEvent(record, CaseEventTypes.ToolResult, new
        {
            tool,
            ok = true,
            result,
            resultRef = stored.ObjectId,
            sha256 = stored.Sha256,
        });
        record.ProcessedEventIds.Add(evt.EventId);
        record.Status = CaseStatus.Active;
        Feed(record.Id, $"Tool {tool} returned.", "ambient");
        _diagnostics.Write(_clock.UtcNow, "info", "CaseRuntime", "tool_result",
            caseId: record.Id, caseVersion: record.Version, status: record.Status, resultRef: stored.ObjectId);
    }

    private object RunPromotedTool(CaseRecord record, string tool, CaseMove move)
    {
        if (_tools is null)
            throw new InvalidOperationException($"Unknown tool '{tool}'.");
        var args = move.Args
            .Where(kv => kv.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(kv => kv.Key, kv => kv.Value.GetString() ?? "", StringComparer.Ordinal);
        // Also accept non-string JSON by raw text for flexibility.
        foreach (var (k, v) in move.Args)
        {
            if (args.ContainsKey(k)) continue;
            args[k] = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.GetRawText();
        }

        var run = _tools.RunPromotedAsync(tool, args, record.ApprovedObjective ?? tool, record.Id, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!run.Ok)
            throw new InvalidOperationException(run.Error ?? "tool failed");
        return JsonSerializer.Deserialize<JsonElement>(run.ResultJson!);
    }

    /// <summary>
    /// Two-stage tool build: generalize → draft+test → propose promote (approval shows name + manifest).
    /// </summary>
    private void BeginToolBuild(CaseRecord record, CaseMove move, string causedByEventId)
    {
        if (_tools is null || !_tools.CanBuild)
        {
            AppendEvent(record, CaseEventTypes.MoveRejected, new
            {
                move = CaseMove.Build,
                name = move.Name,
                reason = "Tool build is not available (no drafter/runner bound).",
            });
            Feed(record.Id, "Tool build is not configured.", "alert");
            record.Status = CaseStatus.Active;
            return;
        }

        var proposedName = string.IsNullOrWhiteSpace(move.Name) ? "world_clock" : move.Name;
        var justification = move.Text;
        if (move.Args.TryGetValue("justification", out var j) && j.ValueKind == JsonValueKind.String)
            justification = j.GetString() ?? justification;
        var inputs = CaseTools.ArgString(move.Args, "inputs");
        var outputs = CaseTools.ArgString(move.Args, "outputs");
        var ask = record.ApprovedObjective ?? justification;

        AppendEvent(record, CaseEventTypes.ToolCalled, new
        {
            tool = "build",
            stage = "generalize_draft_test",
            name = proposedName,
            causedByEventId,
        });

        ToolBuildResult build;
        try
        {
            build = _tools.BuildToolAsync(proposedName, ask, justification, inputs, outputs, record.Id, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppendEvent(record, CaseEventTypes.ToolResult, new { tool = "build", ok = false, error = ex.Message });
            Feed(record.Id, "Tool build failed.", "alert");
            record.Status = CaseStatus.Active;
            return;
        }

        if (!build.Ok || build.Package is null || build.Generalization is null)
        {
            AppendEvent(record, CaseEventTypes.ToolResult, new { tool = "build", ok = false, error = build.Summary });
            Feed(record.Id, "Tool build failed: " + Clip(build.Summary, 120), "alert");
            record.Status = CaseStatus.Active;
            return;
        }

        var pkg = build.Package;
        var gen = build.Generalization;
        var buildEvt = AppendEvent(record, CaseEventTypes.ToolResult, new
        {
            tool = "build",
            ok = true,
            stage = "tested_draft",
            name = pkg.Name,
            generalization = gen,
            manifest = new
            {
                pkg.Name,
                pkg.Description,
                arguments = pkg.Arguments,
                hostFunctions = pkg.HostFunctionNames,
                tests = pkg.Tests.Select(t => t.Args).ToList(),
                sourceSha256 = pkg.SourceSha256,
                tested = pkg.Tested,
            },
            summary = build.Summary,
        });
        record.ProcessedEventIds.Add(buildEvt.EventId);
        Feed(record.Id, $"Drafted and tested '{pkg.Name}' — approval needed to promote.", "proposal");

        // Approval card: final name + manifest (promotion only after evaluation already done).
        var promoteArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(ToolCapabilities.PromoteTool),
            ["name"] = JsonSerializer.SerializeToElement(pkg.Name),
            ["reason"] = JsonSerializer.SerializeToElement(justification),
            ["description"] = JsonSerializer.SerializeToElement(pkg.Description),
            ["manifest"] = JsonSerializer.SerializeToElement(new
            {
                pkg.Name,
                pkg.Description,
                arguments = pkg.Arguments.Select(a => new { a.Name, a.Description, a.Required }),
                hostFunctions = pkg.HostFunctionNames,
                testCount = pkg.Tests.Count,
                counterexamples = gen.Counterexamples,
                sourceSha256 = pkg.SourceSha256,
            }),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("promote-tool-" + pkg.Name + "-" + record.Id),
        };
        ProposeOperation(record, new CaseMove
        {
            Type = CaseMove.Propose,
            Name = ToolCapabilities.PromoteTool,
            Text = $"Promote the tool '{pkg.Name}'",
            Args = promoteArgs,
        }, buildEvt.EventId);
    }

    private void ExecuteWorkflow(CaseRecord record, CaseMove move, string causedByEventId)
    {
        if (_tools is null)
        {
            AppendEvent(record, CaseEventTypes.MoveRejected, new
            {
                move = CaseMove.RunWorkflow,
                name = move.Name,
                reason = "Workflow runtime is not bound.",
            });
            record.Status = CaseStatus.Active;
            return;
        }

        var name = string.IsNullOrWhiteSpace(move.Name) ? CaseTools.ArgString(move.Args, "name") : move.Name;
        AppendEvent(record, CaseEventTypes.ToolCalled, new { tool = "run_workflow", workflow = name, args = move.Args, causedByEventId });

        var def = _tools.Workflows.Promoted(name);
        if (def is null)
        {
            var fail = AppendEvent(record, CaseEventTypes.ToolResult, new { tool = "run_workflow", ok = false, error = $"No promoted workflow '{name}'." });
            record.ProcessedEventIds.Add(fail.EventId);
            record.Status = CaseStatus.Active;
            return;
        }

        var inputs = move.Args
            .Where(kv => kv.Key is not "name" and not "resume")
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ValueKind == JsonValueKind.String ? (kv.Value.GetString() ?? "") : kv.Value.GetRawText(),
                StringComparer.Ordinal);
        Dictionary<string, string>? resume = null;
        if (move.Args.TryGetValue("resume", out var resumeEl) && resumeEl.ValueKind == JsonValueKind.Object)
        {
            resume = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in resumeEl.EnumerateObject())
                resume[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.GetRawText();
        }

        var run = Relay.Core.Workflows.WorkflowBuilder.Run(def, inputs, resume);
        var stored = _objects.PutJson(new { workflow = name, ok = run.Passed, detail = run.Detail, values = run.Values, permissions = def.PermissionUnion });
        var evt = AppendEvent(record, CaseEventTypes.ToolResult, new
        {
            tool = "run_workflow",
            workflow = name,
            ok = run.Passed,
            result = new { run.Detail, values = run.Values },
            resultRef = stored.ObjectId,
        });
        record.ProcessedEventIds.Add(evt.EventId);
        record.Status = CaseStatus.Active;
        Feed(record.Id, run.Passed ? $"Workflow '{name}' ran." : $"Workflow '{name}' failed.", run.Passed ? "ambient" : "alert");
    }

    private void ProposeOperation(CaseRecord record, CaseMove move, string causedByEventId)
    {
        var capability = move.Name;
        if (move.Args.TryGetValue("capability", out var capEl) && capEl.ValueKind == JsonValueKind.String)
            capability = capEl.GetString() ?? capability;

        if (capability is ResearchCapabilities.Search && !SearchAvailable)
        {
            AppendEvent(record, CaseEventTypes.MoveRejected, new
            {
                move = CaseMove.Propose,
                name = capability,
                reason = "No search adapter bound; cannot claim online research.",
            });
            Feed(record.Id, "Online search is not configured.", "alert");
            record.Status = CaseStatus.Active;
            return;
        }

        var capabilityVersion = 1;
        if (move.Args.TryGetValue("capabilityVersion", out var verEl) && verEl.TryGetInt32(out var v))
            capabilityVersion = v;

        var idempotencyKey = Ulid.NewUlid(_clock.UtcNow);
        if (move.Args.TryGetValue("idempotencyKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            idempotencyKey = keyEl.GetString() ?? idempotencyKey;

        var existing = _operations.TryFindByIdempotencyKey(idempotencyKey);
        if (existing is not null && existing.CaseId == record.Id
            && existing.Status is not OperationStatus.Denied and not OperationStatus.Cancelled)
        {
            AppendEvent(record, CaseEventTypes.DuplicateIgnored, new { operationId = existing.OperationId, idempotencyKey });
            if (!record.PendingOperationIds.Contains(existing.OperationId))
                record.PendingOperationIds.Add(existing.OperationId);
            record.Status = existing.Status == OperationStatus.Completed
                ? CaseStatus.Active
                : CaseStatus.Waiting;
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
        Feed(record.Id, $"Approval needed: {capability}", "proposal");

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
            Feed(record.Id, $"Approved {envelope.Capability}.", "proposal");
            _diagnostics.Write(now, "info", "CaseRuntime", "operation_approved",
                caseId: record.Id, caseVersion: record.Version, operationId: operationId, status: envelope.Status);
            return envelope;
        }
    }

    /// <summary>
    /// Edits an awaiting envelope's arguments. Creates a new operation version with a new canonical
    /// hash that requires fresh approval; the previous envelope is cancelled.
    /// </summary>
    public OperationEnvelope EditOperation(string operationId, Dictionary<string, JsonElement> newArguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var previous = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(previous.CaseId)
                ?? throw new InvalidOperationException($"Case '{previous.CaseId}' not found.");

            if (previous.Status != OperationStatus.AwaitingApproval)
                throw new InvalidOperationException($"Only awaiting_approval operations can be edited (was '{previous.Status}').");

            previous.Status = OperationStatus.Cancelled;
            _operations.Save(previous);
            _projections.UpsertOperation(previous);
            record.PendingOperationIds.Remove(previous.OperationId);

            var now = _clock.UtcNow;
            var newId = Ulid.NewUlid(now);
            var edited = new OperationEnvelope
            {
                OperationId = newId,
                CaseId = previous.CaseId,
                CaseVersion = record.Version,
                CausedByEventId = previous.CausedByEventId,
                Capability = previous.Capability,
                CapabilityVersion = previous.CapabilityVersion + 1,
                Arguments = new Dictionary<string, JsonElement>(newArguments, StringComparer.Ordinal),
                InputRefs = previous.InputRefs.ToList(),
                RequestedScope = new Dictionary<string, JsonElement>(previous.RequestedScope, StringComparer.Ordinal),
                GrantedScope = new Dictionary<string, JsonElement>(previous.GrantedScope, StringComparer.Ordinal),
                IdempotencyKey = previous.IdempotencyKey + ":edit:" + previous.CapabilityVersion,
                Preconditions = previous.Preconditions.ToList(),
                Status = OperationStatus.AwaitingApproval,
            };
            edited.CanonicalHashValue = edited.CanonicalHash();
            _operations.Save(edited);
            _projections.UpsertOperation(edited);
            record.PendingOperationIds.Add(newId);
            record.Status = CaseStatus.Waiting;

            var evt = AppendEvent(record, CaseEventTypes.OperationEdited, new
            {
                previousOperationId = previous.OperationId,
                operationId = newId,
                previousHash = previous.CanonicalHashValue,
                canonicalHash = edited.CanonicalHashValue,
                capabilityVersion = edited.CapabilityVersion,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            PersistRecord(record);
            Feed(record.Id, $"Edited proposal {previous.Capability} (new approval required).", "proposal");
            _diagnostics.Write(now, "info", "CaseRuntime", "operation_edited",
                caseId: record.Id, caseVersion: record.Version, operationId: newId, status: edited.Status);
            return edited;
        }
    }

    /// <summary>Rejects an awaiting operation; the case becomes active so the mind may continue or stop.</summary>
    public OperationEnvelope RejectOperation(string operationId, string? reason = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var envelope = _operations.TryLoad(operationId)
                ?? throw new InvalidOperationException($"Operation '{operationId}' not found.");
            var record = _cases.TryLoadRecord(envelope.CaseId)
                ?? throw new InvalidOperationException($"Case '{envelope.CaseId}' not found.");

            if (envelope.Status is OperationStatus.Denied)
                return envelope;

            if (envelope.Status is not OperationStatus.AwaitingApproval and not OperationStatus.Approved)
                throw new InvalidOperationException($"Operation status '{envelope.Status}' cannot be rejected.");

            var now = _clock.UtcNow;
            envelope.Status = OperationStatus.Denied;
            _operations.Save(envelope);
            _projections.UpsertOperation(envelope);
            _projections.UpsertApproval(Ulid.NewUlid(now), operationId, record.Id, envelope.CanonicalHashValue ?? envelope.CanonicalHash(), now, "denied");

            var evt = AppendEvent(record, CaseEventTypes.OperationDenied, new
            {
                operationId,
                reason = reason ?? "rejected",
            });
            record.ProcessedEventIds.Add(evt.EventId);
            record.PendingWaits.Clear();
            // Keep the denied id visible to the mind via pending list, but case is free to continue.
            record.Status = CaseStatus.Active;
            PersistRecord(record);
            _ready.TryEnqueue(record.Id, ReadyPriority.UserReplyOrApproval);
            Feed(record.Id, $"Rejected {envelope.Capability}.", "proposal");
            _diagnostics.Write(now, "info", "CaseRuntime", "operation_denied",
                caseId: record.Id, caseVersion: record.Version, operationId: operationId, status: envelope.Status);
            return envelope;
        }
    }

    /// <summary>Cancels a case. Pending not-yet-started commands are never dispatched; late results may still be accepted for audit.</summary>
    public CaseRecord CancelCase(string caseId, string? reason = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var record = _cases.TryLoadRecord(caseId)
                ?? throw new InvalidOperationException($"Case '{caseId}' not found.");
            if (record.Status is CaseStatus.Cancelled or CaseStatus.Completed)
                return Clone(record);

            foreach (var opId in record.PendingOperationIds.ToList())
            {
                var op = _operations.TryLoad(opId);
                if (op is null) continue;
                if (op.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Requested)
                {
                    op.Status = OperationStatus.Cancelled;
                    _operations.Save(op);
                    _projections.UpsertOperation(op);
                }
            }

            foreach (var cmdId in record.PendingCommandIds.ToList())
            {
                var cmd = _commands.TryLoad(cmdId);
                if (cmd is null) continue;
                if (cmd.Status == RuntimeCommandStatus.Pending)
                {
                    cmd.Status = RuntimeCommandStatus.Cancelled;
                    cmd.Error = "case_cancelled";
                    _commands.Save(cmd);
                    record.PendingCommandIds.Remove(cmdId);
                }
            }

            record.Status = CaseStatus.Cancelled;
            record.Result = reason ?? "cancelled";
            AppendEvent(record, CaseEventTypes.CaseCancelled, new { reason = reason ?? "cancelled" });
            PersistRecord(record);
            _ready.Complete(caseId);
            Feed(caseId, "Case cancelled.", "alert");
            return Clone(record);
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
                stale = caseWasCancelled,
            });
            record.ProcessedEventIds.Add(evt.EventId);
            if (caseWasCancelled)
                record.Status = CaseStatus.Cancelled;
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

    private void Feed(string? caseId, string text, string? level)
    {
        _projections.InsertFeedItem(Ulid.NewUlid(_clock.UtcNow), caseId, _clock.UtcNow, text, level);
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

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inference.Dispose();
        _dispatcher.Dispose();
        _projections.Dispose();
    }
}
