using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Captures;
using Relay.Core.Config;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Recovery;
using Relay.Core.Sessions;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using Relay.Core.Time;

namespace Relay.Core.Session;

/// <summary>
/// Owns the primary state and performs every side effect of the capture slice: ledger records,
/// crash-safe draft persistence, transcript timers, and recovery handling.
/// Single-threaded: the host must call it, and run scheduler callbacks, on one thread.
///
/// Ordering rule for durability: the ledger record is written before any staging file is
/// removed, so a crash between the two leaves a redundant draft (harmless, detected at start)
/// rather than a missing record.
/// </summary>
public sealed partial class SessionCoordinator
{
    private const int ActivityLimit = 400;

    private readonly CoordinatorServices _services;
    private readonly Execution.Executor _executor;
    private readonly Policy.CapabilityIssuer _capabilities = new();
    private readonly ILedger _ledger;
    private readonly IDraftStore _drafts;
    private readonly IDraftNoteStore _notes;
    private readonly SessionStore _sessions;
    private readonly IClock _clock;
    private readonly IScheduler _scheduler;
    private readonly RelaySettings _settings;
    private readonly ICaptureHost _host;
    private readonly DataRoot _root;
    private readonly string _appVersion;
    private readonly int _processId;
    private readonly SettingsStore.LoadResult _settingsLoad;

    private RelayState _state = RelayState.Starting;
    private CaptureDraft? _draft;                 // the active capture (NoteCapture/CommandCapture/AwaitingTranscript/Organizing)
    private string? _draftCommittedEventId;       // set once capture.committed has been written for _draft (idempotent retry)
    private CaptureDraft? _cancelledDraft;        // memory only, never written (contract §3.4)
    private CaptureDraft? _interruptedDraft;      // found at startup; file still in staging
    private string? _lastInstruction;
    private string? _lastInstructionCaptureId;
    private bool _surfaceFocused;
    private bool _retryable;
    private bool _awaitTimedOut;
    private bool _stabilizationPending;
    private int _awaitExtensions;
    private int _awaitGeneration;
    private string? _notice;
    private string? _receipt;
    private IncidentInfo? _incident;
    private LedgerHealth _ledgerHealth;
    private HotkeyStatus _noteKey;
    private HotkeyStatus _commandKey;
    private readonly List<ReviewItem> _staticReview = new();
    private readonly List<ActivityEntry> _activity = new();
    private SessionRecord? _sessionRecord;
    private bool _shutDown;

    private IDisposable? _timeoutTimer;
    private IDisposable? _stabilizationTimer;
    private IDisposable? _receiptTimer;
    private IDisposable? _draftPersistTimer;
    private bool _draftDirty;

    public SessionCoordinator(
        DataRoot root,
        ILedger ledger,
        LedgerVerification verificationAtOpen,
        IDraftStore drafts,
        IDraftNoteStore notes,
        SessionStore sessions,
        SettingsStore.LoadResult settingsLoad,
        ICaptureHost host,
        IClock clock,
        IScheduler scheduler,
        string appVersion,
        int processId,
        CoordinatorServices services)
    {
        _root = root;
        _services = services;
        _executor = new Execution.Executor(root, services.Registry, services.Roots, notes, clock, _capabilities)
        {
            Workers = services.Workers,
            External = services.External,
            Tools = services.Tools,
        };
        _executor.SelfChange = new SelfChange.SelfChangeRuntime(root, PreferenceStore, ChangeSets, () => clock.UtcNow);
        _ledger = ledger;
        _drafts = drafts;
        _notes = notes;
        _sessions = sessions;
        _settingsLoad = settingsLoad;
        _settings = settingsLoad.Settings;
        _host = host;
        _clock = clock;
        _scheduler = scheduler;
        _appVersion = appVersion;
        _processId = processId;
        _ledgerHealth = verificationAtOpen.Health;
        _noteKey = new HotkeyStatus("NOTE_KEY", _settings.Hotkeys.NoteKey, false, "not registered yet", _settings.Hotkeys.Scope);
        _commandKey = new HotkeyStatus("COMMAND_KEY", _settings.Hotkeys.CommandKey, false, "not registered yet", _settings.Hotkeys.Scope);

        foreach (var record in verificationAtOpen.Records.Skip(Math.Max(0, verificationAtOpen.Records.Count - ActivityLimit)))
        {
            _activity.Add(ActivityFormatter.Format(record));
        }
        _ledger.Appended += OnAppended;
    }

    public event Action? Changed;

    public RelayState State => _state;
    public string SessionId => _ledger.SessionId;

    // ----------------------------------------------------------------------------------------
    // Startup
    // ----------------------------------------------------------------------------------------

    public void Start(RecoveryReport report)
    {
        if (_state != RelayState.Starting) throw new InvalidOperationException("Start may only be called once.");

        _sessionRecord = new SessionRecord
        {
            SessionId = _ledger.SessionId,
            ProcessId = _processId,
            AppVersion = _appVersion,
            StartedAt = _clock.UtcNow,
        };
        try { _sessions.Write(_sessionRecord); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = "Could not write the session record: " + ex.Message;
        }

        if (report.QuarantinePath is not null)
        {
            Append(EventTypes.LedgerRepaired, new
            {
                quarantinedBytes = report.QuarantinedBytes,
                quarantinePath = report.QuarantinePath,
                recordsKept = report.Verification.RecordCount,
            });
        }

        Append(EventTypes.SessionStarted, new
        {
            sessionId = _ledger.SessionId,
            pid = _processId,
            appVersion = _appVersion,
            dataRoot = _root.Path,
            settingsHash = _settingsLoad.Hash,
        });

        Append(EventTypes.LedgerVerified, new
        {
            records = report.Verification.RecordCount,
            lastHash = report.Verification.LastHash,
            health = report.Verification.Health.ToString(),
            reason = report.Verification.Reason,
        });

        Append(EventTypes.SettingsLoaded, new { path = _root.SettingsPath, hash = _settingsLoad.Hash, createdDefault = _settingsLoad.CreatedDefault });
        foreach (var problem in _settingsLoad.Problems)
        {
            Append(EventTypes.SettingsInvalid, new { problem });
            _staticReview.Add(new ReviewItem(ReviewItemKind.SettingsProblem, "Setting rejected, default in effect", problem));
        }

        // Relay is the mind. Nothing answers in its place, so when it cannot be reached, say so once and plainly.
        if (_settings.Orchestrator.Mode == OrchestratorSettings.Mind && _services.Mind is null)
        {
            var why = _settings.Model.Enabled ? $"the model at {_settings.Model.Endpoint} could not be reached" : "the model is switched off in Settings";
            Append(EventTypes.MindUnavailable, new { reason = _settings.Model.Enabled ? "unreachable" : "disabled", endpoint = _settings.Model.Endpoint, model = _settings.Model.Model });
            _staticReview.Add(new ReviewItem(ReviewItemKind.MindUnavailable, "Relay has no mind",
                $"Relay runs on one local model and {why}. Until it is on, Relay records what you say and nothing else: it will not read conversations, answer, or act."));
        }

        foreach (var crashed in report.CrashedSessions)
        {
            Append(EventTypes.SessionCrashDetected, new
            {
                crashedSessionId = crashed.SessionId,
                crashedPid = crashed.ProcessId,
                crashedStartedAt = crashed.StartedAt,
            });
            crashed.EndedAt = _clock.UtcNow;
            crashed.EndedBy = "recovery";
            try { _sessions.Write(crashed); } catch (IOException) { /* visible via ledger; not fatal */ }
        }

        if (report.InterruptedDraft is { } draft)
        {
            if (report.InterruptedDraftAlreadyResolved)
            {
                // The record exists; the staging copy is a leftover from a crash between append and cleanup.
                Append(EventTypes.CaptureInterruptedFound, new
                {
                    captureId = draft.CaptureId,
                    mode = draft.ModeWire,
                    chars = draft.Text.Length,
                    alreadyResolved = true,
                    resolution = report.InterruptedDraftResolution,
                });
                try { _drafts.RemoveCurrent(); } catch (IOException) { }
            }
            else
            {
                _interruptedDraft = draft;
                Append(EventTypes.CaptureInterruptedFound, new
                {
                    captureId = draft.CaptureId,
                    mode = draft.ModeWire,
                    chars = draft.Text.Length,
                    startedAt = draft.StartedAt,
                    updatedAt = draft.UpdatedAt,
                    alreadyResolved = false,
                });
            }
        }

        DetectInterruptedWork();
        DetectInterruptedStream();

        if (_state == RelayState.Locked)
        {
            // A ledger write already failed during startup; nothing more to do.
            Notify();
            return;
        }

        if (report.Verification.Health == LedgerHealth.IntegrityFailure)
        {
            var v = report.Verification;
            Append(EventTypes.LedgerIntegrityFailed, new { brokenSeq = v.BrokenSeq, reason = v.Reason, recordsVerified = v.RecordCount });
            _incident = new IncidentInfo(
                "ledger_integrity_failed",
                $"Ledger integrity failure at record {v.BrokenSeq}",
                $"{v.Reason}\n\nThe ledger file {_root.LedgerPath} was modified outside Relay or damaged on disk. "
                + $"Records 1–{v.RecordCount} verified; the chain is broken after that. New records continue from the current tail so the break stays evident. "
                + "Inspect the file before unlocking.",
                _clock.UtcNow,
                WriteIncidentFile("ledger_integrity_failed", v.Reason ?? "unknown", null));
            Apply(Trigger.RecoveryLocked);
            Append(EventTypes.LockEngaged, new { reason = "ledger_integrity_failed", brokenSeq = v.BrokenSeq });
        }
        else
        {
            Apply(Trigger.RecoveryCompleted);
        }
        Notify();
    }

    public void ReportHotkey(string name, string chord, bool registered, string? error, string scope = HotkeySettings.GlobalScope)
    {
        var status = new HotkeyStatus(name, chord, registered, error, scope);
        if (name == "NOTE_KEY") _noteKey = status; else _commandKey = status;
        if (registered)
        {
            Append(EventTypes.HotkeyRegistered, new { name, chord, scope });
        }
        else
        {
            Append(EventTypes.HotkeyRegistrationFailed, new { name, chord, scope, error });
            _staticReview.Add(new ReviewItem(ReviewItemKind.HotkeyProblem, $"{name} ({chord}) is not active", error ?? "Registration failed."));
        }
        Notify();
    }

    public void ReportStorageAcl(bool applied, string? error)
    {
        if (applied) Append(EventTypes.StorageAclApplied, new { path = _root.Path });
        else Append(EventTypes.StorageAclFailed, new { path = _root.Path, error });
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // Primary toggles
    // ----------------------------------------------------------------------------------------

    public void PressNoteKey() => PressKey(Trigger.NoteKey, CaptureMode.Note, "NOTE_KEY");
    public void PressCommandKey() => PressKey(Trigger.CommandKey, CaptureMode.Command, "COMMAND_KEY");

    private void PressKey(Trigger trigger, CaptureMode mode, string keyName)
    {
        if (_shutDown) return;
        var before = _state;
        var transition = Apply(trigger, keyName);
        if (!transition.Accepted) { Notify(); return; }

        if (before is RelayState.Idle or RelayState.Completed && _state is RelayState.NoteCapture or RelayState.CommandCapture)
        {
            BeginCapture(mode);
        }
        else if (before is RelayState.NoteCapture or RelayState.CommandCapture && _state == RelayState.AwaitingTranscript)
        {
            RequestStop();
        }
        Notify();
    }

    private void BeginCapture(CaptureMode mode)
    {
        _receiptTimer?.Dispose();
        _receipt = null;
        _notice = null;
        _awaitTimedOut = false;
        _awaitExtensions = 0;
        _stabilizationPending = false;
        _draftCommittedEventId = null;

        var now = _clock.UtcNow;
        _draft = new CaptureDraft
        {
            CaptureId = Ulid.NewUlid(now),
            ModeWire = mode.WireName(),
            SessionId = _ledger.SessionId,
            StartedAt = now,
            UpdatedAt = now,
            Text = "",
            PreviousForegroundProcess = SafeForegroundProcess(),
        };

        if (mode == CaptureMode.Note && ListeningEnabled)
        {
            // Listening: no capture record with text, no draft file. The stream's own records carry ids and sizes only.
            BeginStream(_draft);
            _host.PrepareCaptureSurface();
            _surfaceFocused = true;
            return;
        }

        if (Append(EventTypes.CaptureStarted, new
        {
            captureId = _draft.CaptureId,
            mode = _draft.ModeWire,
            previousForegroundProcess = _draft.PreviousForegroundProcess,
        }) is null) return;

        PersistDraftNow();
        _host.PrepareCaptureSurface();
        _surfaceFocused = true;
    }

    private void RequestStop()
    {
        if (_draft is null) return;
        if (Append(EventTypes.CaptureStopRequested, new { captureId = _draft.CaptureId, chars = _draft.Text.Length, listening = IsStreaming(_draft) }) is null) return;
        if (IsStreaming(_draft))
        {
            // Listening has no transcript to wait for: whatever Flow still delivers in the next moment is segmented, then the stream closes.
            StartAwaitingTimers(restartTimeout: true);
            if (_draft.Text.Length == 0) OnTranscriptStable(_awaitGeneration);
            return;
        }
        StartAwaitingTimers(restartTimeout: true);
    }

    private void StartAwaitingTimers(bool restartTimeout)
    {
        var generation = ++_awaitGeneration;
        if (restartTimeout)
        {
            _timeoutTimer?.Dispose();
            _timeoutTimer = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Capture.TranscriptTimeoutMs), () => OnTranscriptTimeout(generation));
        }
        if (_draft is { Text.Length: > 0 }) RestartStabilization();
    }

    private void RestartStabilization()
    {
        var generation = _awaitGeneration;
        _stabilizationTimer?.Dispose();
        _stabilizationPending = true;
        _stabilizationTimer = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Capture.StabilizationMs), () => OnTranscriptStable(generation));
    }

    private void OnTranscriptStable(int generation)
    {
        _stabilizationTimer = null;
        _stabilizationPending = false;
        if (generation != _awaitGeneration || _state != RelayState.AwaitingTranscript || _draft is null || (_draft.Text.Length == 0 && !IsStreaming(_draft))) return;
        if (Append(EventTypes.CaptureTranscriptStable, new { captureId = _draft.CaptureId, chars = _draft.Text.Length }) is null) { Notify(); return; }
        StopAwaitingTimers();
        if (Apply(Trigger.TranscriptStable).Accepted) Organize();
        Notify();
    }

    private void OnTranscriptTimeout(int generation)
    {
        _timeoutTimer = null;
        if (generation != _awaitGeneration || _state != RelayState.AwaitingTranscript || _draft is null) return;
        if (_draft.Text.Length > 0 || IsStreaming(_draft))
        {
            // Text is present but never went quiet; treat the timeout as the stability boundary.
            OnTranscriptStable(generation);
            return;
        }
        if (Append(EventTypes.CaptureTranscriptTimeout, new { captureId = _draft.CaptureId, waitedMs = _settings.Capture.TranscriptTimeoutMs, extensions = _awaitExtensions }) is null) { Notify(); return; }
        _awaitTimedOut = true;
        Apply(Trigger.TranscriptTimeout);
        _notice = "No transcript arrived. If Flow shows your dictation, recover it from Flow and paste it here, or retry waiting. Relay does not read the clipboard.";
        Notify();
    }

    private void StopAwaitingTimers()
    {
        _awaitGeneration++;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        _stabilizationTimer?.Dispose();
        _stabilizationTimer = null;
        _stabilizationPending = false;
    }

    // ----------------------------------------------------------------------------------------
    // Capture surface events
    // ----------------------------------------------------------------------------------------

    public void TextChanged(string text)
    {
        if (_draft is null || !_state.IsCapturing()) return;
        if (string.Equals(_draft.Text, text, StringComparison.Ordinal)) return;

        _draft.Text = text;
        _draft.UpdatedAt = _clock.UtcNow;
        if (_stream is { } stream && stream.StreamId == _draft.CaptureId)
        {
            // Listening: the words go to the segmenter and the rolling window, never to a draft file.
            StreamTextChanged(stream, text);
        }
        else
        {
            _draftDirty = true;
            _draftPersistTimer ??= _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Capture.DraftPersistDebounceMs), () =>
            {
                _draftPersistTimer = null;
                if (_draftDirty) PersistDraftNow();
                Notify();
            });
        }

        if (_state == RelayState.AwaitingTranscript)
        {
            if (_awaitTimedOut && text.Length > 0)
            {
                _awaitTimedOut = false;
                _notice = null;
            }
            if (text.Length > 0) RestartStabilization();
            else { _stabilizationTimer?.Dispose(); _stabilizationTimer = null; _stabilizationPending = false; }
        }
        Notify();
    }

    public void FocusChanged(bool focused)
    {
        if (_surfaceFocused == focused) return;
        _surfaceFocused = focused;
        if (_draft is not null && _state.IsCapturing())
        {
            Append(focused ? EventTypes.CaptureFocusRegained : EventTypes.CaptureFocusLost, new { captureId = _draft.CaptureId });
        }
        Notify();
    }

    public void Cancel()
    {
        if (_state.IsTurnActive()) { CancelForegroundTask(); Notify(); return; }
        if (_draft is null || !_state.CanCancelFrom()) { _notice = TransitionTable.Next(_state, Trigger.Cancel).Message; Notify(); return; }
        var stateAtCancel = _state;
        var transition = Apply(Trigger.Cancel);
        if (!transition.Accepted) { Notify(); return; }

        StopAwaitingTimers();
        _draftPersistTimer?.Dispose();
        _draftPersistTimer = null;

        var draft = _draft;
        _draft = null;
        _draftCommittedEventId = null;
        _awaitTimedOut = false;

        if (_stream is { } stream && stream.StreamId == draft.CaptureId)
        {
            // Listening cancelled: the window is dropped; excerpts and tasks already raised stand on their own.
            Append(EventTypes.CaptureCancelled, new { captureId = draft.CaptureId, mode = draft.ModeWire, chars = draft.Text.Length, stateAtCancel = stateAtCancel.Label(), listening = true });
            CompleteStream(stream, "cancelled");
            _cancelledDraft = null;
            Notify();
            return;
        }

        Append(EventTypes.CaptureCancelled, new
        {
            captureId = draft.CaptureId,
            mode = draft.ModeWire,
            chars = draft.Text.Length,
            stateAtCancel = stateAtCancel.Label(),
        });

        try { _drafts.RemoveCurrent(); }
        catch (IOException ex) { _notice = "Cancelled, but the staging draft could not be removed: " + ex.Message; }

        _cancelledDraft = draft.Text.Length > 0 ? draft : null;
        Notify();
    }

    public void SubmitNow()
    {
        if (_state != RelayState.AwaitingTranscript || _draft is null || _draft.Text.Length == 0)
        {
            _notice = _draft is { Text.Length: 0 } ? "Nothing has arrived yet." : TransitionTable.Next(_state, Trigger.SubmitNow).Message;
            Notify();
            return;
        }
        if (Append(EventTypes.CaptureSubmittedEarly, new { captureId = _draft.CaptureId, chars = _draft.Text.Length }) is null) { Notify(); return; }
        StopAwaitingTimers();
        if (Apply(Trigger.SubmitNow).Accepted) Organize();
        Notify();
    }

    public void RetryWait()
    {
        if (_state != RelayState.AwaitingTranscript || _draft is null) { Notify(); return; }
        if (Append(EventTypes.CaptureWaitExtended, new { captureId = _draft.CaptureId, extensions = _awaitExtensions + 1 }) is null) { Notify(); return; }
        _awaitExtensions++;
        _awaitTimedOut = false;
        _notice = null;
        Apply(Trigger.RetryWait);
        StartAwaitingTimers(restartTimeout: true);
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // Organizing (local validation + storage, no model)
    // ----------------------------------------------------------------------------------------

    private void Organize()
    {
        if (_state != RelayState.Organizing || _draft is null) return;
        var draft = _draft;

        if (_stream is { } stream && stream.StreamId == draft.CaptureId)
        {
            // Listening ends with one last pass over what is still unread; the stream closes when it returns.
            FinishStream(stream);
            return;
        }

        try
        {
            if (_draftCommittedEventId is null)
            {
                var committed = Append(EventTypes.CaptureCommitted, new
                {
                    captureId = draft.CaptureId,
                    mode = draft.ModeWire,
                    chars = draft.Text.Length,
                    sha256 = Sha256Hex(draft.Text),
                    startedAt = draft.StartedAt,
                    stoppedAt = _clock.UtcNow,
                    source = "capture-surface",
                    text = draft.Text,
                });
                if (committed is null) return; // locked
                _draftCommittedEventId = committed.Id;
                _services.Index.IndexCapture(committed);
            }

            string receipt;
            var startTurn = false;
            if (draft.Mode == CaptureMode.Note)
            {
                receipt = OrganizeNote(draft, _draftCommittedEventId);
                if (receipt.Length == 0) return; // locked
            }
            else
            {
                startTurn = OrchestratorEnabled;
                if (Append(EventTypes.CommandRecorded, new { captureId = draft.CaptureId, sourceEventId = _draftCommittedEventId, chars = draft.Text.Length, executed = false, orchestrator = _settings.Orchestrator.Mode }) is null) return;
                if (!startTurn)
                {
                    _lastInstruction = draft.Text;
                    _lastInstructionCaptureId = draft.CaptureId;
                }
                receipt = "Instruction recorded · orchestrator is off, no plan was made";
            }

            // Only now is the staging copy redundant.
            _draftPersistTimer?.Dispose();
            _draftPersistTimer = null;
            _draftDirty = false;
            try { _drafts.RemoveCurrent(); }
            catch (IOException ex) { _notice = "Stored, but the staging draft could not be removed: " + ex.Message; }

            var sourceEventId = _draftCommittedEventId;
            _draft = null;
            _draftCommittedEventId = null;
            if (startTurn)
            {
                StartCommandTask(draft, sourceEventId);
                return;
            }
            if (Apply(Trigger.OrganizeSucceeded).Accepted) ShowReceipt(receipt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Append(EventTypes.CaptureOrganizeFailed, new { captureId = draft.CaptureId, error = ex.Message, committed = _draftCommittedEventId is not null });
            _incident = new IncidentInfo("organize_failed", "Could not store the capture", ex.ToString(), _clock.UtcNow, null);
            _retryable = true;
            Apply(Trigger.OrganizeFailed);
        }
    }

    /// <summary>
    /// Stores a note capture. With the orchestrator off it becomes one verbatim draft note in staging;
    /// otherwise it is extracted into atomic notes and routed (see the Memory partial). Returns the receipt text, or empty when the ledger locked.
    /// </summary>
    private string OrganizeNote(CaptureDraft draft, string sourceEventId)
    {
        if (OrchestratorEnabled) return OrganizeNoteMemory(draft, sourceEventId);

        var note = new DraftNote(
            Ulid.NewUlid(_clock.UtcNow), draft.CaptureId, sourceEventId, _clock.UtcNow,
            DraftNote.RawCaptureType, DraftNote.DraftStatus, null, DraftNote.UnroutedRouting, null,
            draft.Text, [new SourceSpan(sourceEventId, 0, draft.Text.Length)]);
        var path = _notes.Write(note);
        if (Append(EventTypes.NoteDraftCreated, new { noteId = note.NoteId, captureId = draft.CaptureId, sourceEventId, chars = draft.Text.Length, path }) is null) return "";
        _services.Index.IndexDraft(note);
        return "Saved 1 draft note · orchestrator is off, routing deferred";
    }

    private void ShowReceipt(string receipt)
    {
        _receipt = receipt;
        _receiptTimer?.Dispose();
        _receiptTimer = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Capture.CompletedReceiptMs), () =>
        {
            _receiptTimer = null;
            if (_state == RelayState.Completed) { Apply(Trigger.Dismiss); Notify(); }
        });
    }

    // ----------------------------------------------------------------------------------------
    // Review actions
    // ----------------------------------------------------------------------------------------

    public void CommitInterrupted()
    {
        if (_interruptedDraft is null || _state != RelayState.Idle) { _notice = _interruptedDraft is null ? "No interrupted capture." : "Return to IDLE first."; Notify(); return; }
        _draft = _interruptedDraft;
        _interruptedDraft = null;
        _draftCommittedEventId = null;
        _receiptTimer?.Dispose();
        _receipt = null;
        if (Apply(Trigger.CommitInterrupted).Accepted) Organize();
        Notify();
    }

    public void DiscardInterrupted()
    {
        if (_interruptedDraft is null) { Notify(); return; }
        var draft = _interruptedDraft;
        try
        {
            var path = _drafts.MoveCurrentToDiscarded(draft.CaptureId);
            if (Append(EventTypes.CaptureInterruptedDiscarded, new { captureId = draft.CaptureId, mode = draft.ModeWire, chars = draft.Text.Length, path }) is null) return;
            _interruptedDraft = null;
        }
        catch (IOException ex)
        {
            _notice = "Could not move the interrupted draft: " + ex.Message;
        }
        Notify();
    }

    public void RecoverCancelledDraft()
    {
        if (_cancelledDraft is null || _state != RelayState.Idle) { _notice = _cancelledDraft is null ? "No cancelled draft to recover." : "Return to IDLE first."; Notify(); return; }
        var original = _cancelledDraft;
        _cancelledDraft = null;
        var now = _clock.UtcNow;
        _draft = new CaptureDraft
        {
            CaptureId = Ulid.NewUlid(now),
            ModeWire = original.ModeWire,
            SessionId = _ledger.SessionId,
            StartedAt = original.StartedAt,
            UpdatedAt = now,
            Text = original.Text,
            PreviousForegroundProcess = original.PreviousForegroundProcess,
        };
        _draftCommittedEventId = null;
        if (Append(EventTypes.CaptureDraftRecovered, new { captureId = _draft.CaptureId, originalCaptureId = original.CaptureId, mode = _draft.ModeWire, chars = _draft.Text.Length }) is null) { Notify(); return; }
        PersistDraftNow();
        _receiptTimer?.Dispose();
        _receipt = null;
        if (Apply(Trigger.RecoverDraft).Accepted) Organize();
        Notify();
    }

    public void ForgetCancelledDraft()
    {
        _cancelledDraft = null;
        Notify();
    }

    public void Dismiss()
    {
        if (_state is RelayState.Completed or RelayState.Failed)
        {
            _receiptTimer?.Dispose();
            _receiptTimer = null;
            if (_state == RelayState.Failed && _draft is not null)
            {
                if (_draftCommittedEventId is not null)
                {
                    // The words are already durable in the ledger; only the derived record failed.
                    try { _drafts.RemoveCurrent(); } catch (IOException) { }
                }
                else
                {
                    // Keep the words: the draft file is still in staging, so treat it like an interrupted capture.
                    _interruptedDraft = _draft;
                }
                _draft = null;
                _draftCommittedEventId = null;
            }
            _incident = null;
            _receipt = null;
            Apply(Trigger.Dismiss);
        }
        Notify();
    }

    /// <summary>Retry is offered only when storing the capture itself failed; other failures are dismissed.</summary>
    public bool CanRetry => _state == RelayState.Failed && _draft is not null && _retryable;

    public void Retry()
    {
        if (!CanRetry) { Dismiss(); return; }
        _incident = null;
        _retryable = false;
        if (Apply(Trigger.Retry).Accepted) Organize();
        Notify();
    }

    public void Unlock()
    {
        if (_state != RelayState.Locked) { Notify(); return; }
        var acknowledged = _incident?.Kind ?? "unknown";
        var record = Append(EventTypes.LockReleased, new { acknowledged, by = "user" });
        if (record is null)
        {
            // The ledger still cannot be written; stay locked with the fresh incident.
            Notify();
            return;
        }
        _incident = null;
        _notice = null;
        Apply(Trigger.Unlock);
        Notify();
    }

    // ----------------------------------------------------------------------------------------
    // Failure and shutdown
    // ----------------------------------------------------------------------------------------

    /// <summary>Called by the host's unhandled-exception handlers. Records the failure and stops work.</summary>
    public void ReportFailure(Exception exception, string where)
    {
        var detail = exception.ToString();
        var incidentPath = WriteIncidentFile("app_failed", where + ": " + exception.Message, detail);
        Append(EventTypes.AppFailed, new { where, exceptionType = exception.GetType().FullName, message = exception.Message, incidentPath });
        if (_state is RelayState.Locked) { Notify(); return; }

        foreach (var task in _tasks.Where(t => t.IsLive).ToList())
        {
            task.Timeout?.Dispose();
            task.Timeout = null;
            RequestWorkerStop?.Invoke(task.PendingOperation?.Proposal.ProposalId);
            _services.External?.Stop(task.PendingOperation?.Proposal.ProposalId, "app failed");
            task.Outcome = "failed";
            task.Status = Tasks.TaskStatus.Failed;
            task.CompletedAt = _clock.UtcNow;
            Append(EventTypes.TaskFailed, new { taskId = task.TaskId, origin = task.Origin.Wire(), kind = task.Kind.Wire(), failure = "app_failed", error = exception.Message, pendingOperation = task.PendingOperation?.Proposal.ProposalId });
            WriteDiagnostics(task);
            task.Cts.Cancel();
        }

        if (_stream is { } stream)
        {
            if (stream.WindowDirty) PersistWindow(stream);
            CompleteStream(stream, "failed");
            if (_draft is not null && _draft.CaptureId == stream.StreamId) { _draft = null; _draftCommittedEventId = null; }
        }
        else if (_draft is not null && _state.IsCapturing())
        {
            StopAwaitingTimers();
            if (_draftDirty) PersistDraftNow();
            // The draft stays in staging and becomes an interrupted capture when the user dismisses the failure.
        }
        _retryable = false;
        _incident = new IncidentInfo("app_failed", $"Failure in {where}", detail, _clock.UtcNow, incidentPath);
        Apply(Trigger.Fail);
        Notify();
    }

    public void Shutdown(string reason)
    {
        if (_shutDown) return;
        _shutDown = true;
        _timeoutTimer?.Dispose();
        _stabilizationTimer?.Dispose();
        _receiptTimer?.Dispose();
        _draftPersistTimer?.Dispose();

        if (_stream is { } stream)
        {
            // A clean exit while listening closes the stream; the window is dropped, not kept.
            CompleteStream(stream, "shutdown:" + reason);
        }
        else if (_draft is not null && _state.IsCapturing() && _draftDirty) PersistDraftNow();

        foreach (var task in _tasks.Where(t => t.IsLive).ToList())
        {
            // A clean exit mid-task is a cancellation, recorded as such so startup does not report a crash.
            task.Timeout?.Dispose();
            task.Timeout = null;
            RequestWorkerStop?.Invoke(task.PendingOperation?.Proposal.ProposalId);
            _services.External?.Stop(task.PendingOperation?.Proposal.ProposalId, "shutdown");
            var stage = task.Status.Wire();
            task.Status = Tasks.TaskStatus.Cancelled;
            task.Outcome = "cancelled";
            task.CompletedAt = _clock.UtcNow;
            Append(EventTypes.TaskCancelled, new { taskId = task.TaskId, stage, reason = "shutdown:" + reason });
            WriteDiagnostics(task);
            task.Cts.Cancel();
        }

        Append(EventTypes.SessionEnded, new
        {
            reason,
            finalState = _state.Label(),
            activeCaptureId = _draft is not null && _state.IsCapturing() ? _draft.CaptureId : null,
        });

        if (_sessionRecord is not null)
        {
            _sessionRecord.EndedAt = _clock.UtcNow;
            _sessionRecord.CleanShutdown = true;
            _sessionRecord.EndedBy = reason;
            try { _sessions.Write(_sessionRecord); } catch (IOException) { }
        }
        _ledger.Appended -= OnAppended;
    }

    // ----------------------------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------------------------

    private Transition Apply(Trigger trigger, string? keyName = null)
    {
        var transition = TransitionTable.Next(_state, trigger);
        if (!transition.Accepted)
        {
            _notice = transition.Message;
            if (keyName is not null)
            {
                Append(EventTypes.HotkeyRejected, new { key = keyName, state = _state.Label(), reason = transition.Message });
            }
            return transition;
        }

        var from = _state;
        _state = transition.To;
        if (transition.Changed)
        {
            _notice = null;
            Append(EventTypes.StateChanged, new { from = from.Label(), to = transition.To.Label(), trigger = trigger.ToString() });
        }
        return transition;
    }

    /// <summary>Appends a record, or engages the lock and returns null when the ledger cannot be written.</summary>
    private LedgerRecord? Append(string type, object data)
    {
        try
        {
            return _ledger.Append(type, data);
        }
        catch (LedgerWriteException ex)
        {
            EngageLockWithoutLedger("ledger_write_failed", $"Could not write '{type}' to the ledger", ex.ToString());
            return null;
        }
    }

    private void EngageLockWithoutLedger(string kind, string summary, string detail)
    {
        var path = WriteIncidentFile(kind, summary, detail);
        _incident = new IncidentInfo(kind, summary, detail + "\n\nThe ledger could not be written, so this incident was recorded only in the incidents folder. "
            + "Unlock retries the ledger; if the disk or permissions problem persists Relay stays locked. Any active capture remains in staging\\drafts\\current.json.",
            _clock.UtcNow, path);
        if (_draft is not null && _draftDirty && !IsStreaming(_draft))
        {
            try { _drafts.Write(_draft); _draftDirty = false; } catch (IOException) { }
        }
        StopAwaitingTimers();
        var transition = TransitionTable.Next(_state, Trigger.Lock);
        if (transition.Accepted) _state = RelayState.Locked;
    }

    private string? WriteIncidentFile(string kind, string summary, string? detail)
    {
        try
        {
            Directory.CreateDirectory(_root.IncidentsDirectory);
            var stamp = _clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(_root.IncidentsDirectory, $"{stamp}-{kind}.json");
            var payload = new { kind, summary, detail, at = _clock.UtcNow, sessionId = _ledger.SessionId, state = _state.Label(), pid = _processId };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(payload, RelayJson.Indented));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PersistDraftNow()
    {
        if (_draft is null) return;
        try
        {
            _drafts.Write(_draft);
            _draftDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = "Draft could not be saved to staging: " + ex.Message + " (text is still held in memory)";
            Append(EventTypes.AppFailed, new { where = "draft_persist", exceptionType = ex.GetType().FullName, message = ex.Message, incidentPath = (string?)null });
        }
    }

    private string? SafeForegroundProcess()
    {
        try { return _host.ForegroundProcessName(); }
        catch { return null; }
    }

    private void OnAppended(LedgerRecord record)
    {
        _activity.Add(ActivityFormatter.Format(record));
        if (_activity.Count > ActivityLimit) _activity.RemoveRange(0, _activity.Count - ActivityLimit);
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Notify() => Changed?.Invoke();

    public RelaySnapshot Snapshot
    {
        get
        {
            var review = new List<ReviewItem>();
            if (_incident is { } incident)
            {
                review.Add(new ReviewItem(ReviewItemKind.Incident, incident.Summary, incident.Detail, incident.IncidentFilePath));
            }
            if (_interruptedDraft is { } interrupted)
            {
                review.Add(new ReviewItem(ReviewItemKind.InterruptedCapture,
                    $"Interrupted {interrupted.Mode.Label().ToLowerInvariant()} capture from {interrupted.StartedAt.ToLocalTime():g}",
                    $"{interrupted.Text.Length} character(s) were captured but never stored. Commit stores them exactly as captured; Discard moves the draft to staging\\drafts\\discarded.",
                    interrupted.Text));
            }
            if (_cancelledDraft is { } cancelled)
            {
                review.Add(new ReviewItem(ReviewItemKind.CancelledDraft,
                    $"Cancelled {cancelled.Mode.Label().ToLowerInvariant()} capture ({cancelled.Text.Length} chars)",
                    "Held in memory only. Recover draft stores it as a capture; Forget drops it. It is gone when Relay exits.",
                    cancelled.Text));
            }
            // Proposals awaiting approval are not Review items: they live only in Response, next to the plan that produced them.
            if (_lastInstruction is not null && !OrchestratorEnabled)
            {
                review.Add(new ReviewItem(ReviewItemKind.RecordedInstruction,
                    "Instruction recorded — orchestrator is off",
                    "The exact instruction is preserved in the ledger. No plan was made and no tool ran. Enable the orchestrator in settings to act on instructions.",
                    _lastInstruction));
            }
            review.AddRange(MemoryReviewItems());
            review.AddRange(_recoveryReview);
            review.AddRange(_staticReview);

            return new RelaySnapshot(
                _state,
                _state is RelayState.Idle or RelayState.Locked or RelayState.Starting ? null : _draft?.Mode,
                _draft?.CaptureId,
                _draft?.StartedAt,
                _draft?.Text.Length ?? 0,
                _surfaceFocused,
                _state == RelayState.AwaitingTranscript ? new AwaitingStatus(_awaitTimedOut, _awaitExtensions, _stabilizationPending) : null,
                _notice,
                _receipt,
                _incident,
                review,
                _activity.AsReadOnly(),
                _noteKey,
                _commandKey,
                _ledger.Path,
                _ledger.LastSeq,
                _ledger.LastHash,
                _ledgerHealth,
                _ledger.SessionId,
                _root.Path,
                _appVersion,
                _processId,
                CanRetry,
                ResponseView(),
                ProjectViews(),
                WorkspaceViews(),
                InboxViews(),
                _settings.Orchestrator.Mode,
                PlannerName,
                _settings.Model.Enabled,
                _settings.Model.Enabled ? _settings.Model.Endpoint : null,
                _settings.Model.Enabled ? _settings.Model.Model : null,
                ModelKeyStored,
                _services.Mind is not null,
                TaskViews(),
                Arbiter.Items,
                ListeningView(),
                ListeningEnabled,
                Preferences,
                ChangeSetViews(),
                _services.External?.ProfileNames ?? []);
        }
    }
}

internal static class StateGuards
{
    public static bool CanCancelFrom(this RelayState state)
        => state is RelayState.NoteCapture or RelayState.CommandCapture or RelayState.AwaitingTranscript;
}
