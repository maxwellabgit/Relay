using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Stream;
using Relay.Core.Tasks;

namespace Relay.Core.Session;

/// <summary>
/// Listening. The note chord opens a stream instead of a recording: the surface text is cut into
/// timestamped segments, held in a rolling buffer that expires continuously, and judged by RELAY0
/// every few seconds. Findings persist an excerpt and become observed tasks; everything else is
/// gone within the window. The ledger sees ids, hashes, sizes, tokens and timings — never the words.
/// The full surface text stays in memory for the user to see and is dropped when the stream stops.
/// </summary>
public sealed partial class SessionCoordinator
{
    private sealed class StreamState
    {
        public required string StreamId { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required StreamSegmenter Segmenter { get; init; }
        public required ConversationBuffer Buffer { get; init; }
        public IDisposable? ObserveTimer { get; set; }
        public IDisposable? QuietTimer { get; set; }
        public IDisposable? PersistTimer { get; set; }
        public IDisposable? JudgeTimeout { get; set; }
        public CancellationTokenSource? JudgeCts { get; set; }
        public bool Judging { get; set; }
        public bool Finishing { get; set; }
        public bool WindowDirty { get; set; }
        public int Passes { get; set; }
        public int Findings { get; set; }
        public int Excerpts { get; set; }
        public int Tasks { get; set; }
        public int Segments { get; set; }
        public int Chars { get; set; }
        public DateTimeOffset? LastCheckAt { get; set; }
        public string? LastError { get; set; }
        public string? LastJudge { get; set; }
    }

    private StreamState? _stream;

    /// <summary>Whether the note chord listens (judge on) or dictates a note the old way (judge off).</summary>
    public bool ListeningEnabled => _settings.Judge.Mode != JudgeSettings.Off;

    private bool IsStreaming(Captures.CaptureDraft? draft) => _stream is not null && draft is not null && _stream.StreamId == draft.CaptureId;

    private void BeginStream(Captures.CaptureDraft draft)
    {
        var now = _clock.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(15, Math.Min(_settings.Stream.BufferSeconds, Preferences.Buffer.TotalSeconds > 0 ? Preferences.Buffer.TotalSeconds : _settings.Stream.BufferSeconds)));
        _stream = new StreamState
        {
            StreamId = draft.CaptureId,
            StartedAt = now,
            Segmenter = new StreamSegmenter(),
            Buffer = new ConversationBuffer(window),
        };
        Excerpts.BeginStream();
        Append(EventTypes.StreamStarted, new { streamId = draft.CaptureId, bufferSeconds = window.TotalSeconds, observeIntervalMs = _settings.Stream.ObserveIntervalMs, judge = _services.Judge.Name, judgeMode = _settings.Judge.Mode, previousForegroundProcess = draft.PreviousForegroundProcess });
        ScheduleObserve();
    }

    private void ScheduleObserve()
    {
        var stream = _stream;
        if (stream is null || stream.Finishing) return;
        stream.ObserveTimer?.Dispose();
        stream.ObserveTimer = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Stream.ObserveIntervalMs), () =>
        {
            if (_stream != stream || stream.Finishing) return;
            stream.ObserveTimer = null;
            stream.Buffer.Expire(_clock.UtcNow);
            if (stream.Segmenter.HasPendingSince(_clock.UtcNow, TimeSpan.FromMilliseconds(_settings.Stream.SegmentQuietMs))) AddSegments(stream, stream.Segmenter.FlushPending(_clock.UtcNow));
            JudgeNow(stream, final: false);
            ScheduleObserve();
            Notify();
        });
    }

    /// <summary>The surface text changed while listening: cut what arrived into segments; persist only the window.</summary>
    private void StreamTextChanged(StreamState stream, string text)
    {
        var now = _clock.UtcNow;
        var segments = stream.Segmenter.Feed(text, now);
        AddSegments(stream, segments);
        stream.QuietTimer?.Dispose();
        stream.QuietTimer = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Stream.SegmentQuietMs), () =>
        {
            if (_stream != stream) return;
            stream.QuietTimer = null;
            var flushed = stream.Segmenter.FlushPending(_clock.UtcNow);
            if (flushed.Count > 0) { AddSegments(stream, flushed); Notify(); }
        });
    }

    private void AddSegments(StreamState stream, IReadOnlyList<StreamSegment> segments)
    {
        if (segments.Count == 0) return;
        var now = _clock.UtcNow;
        var urgent = false;
        foreach (var segment in segments)
        {
            stream.Buffer.Append(segment, now);
            stream.Segments++;
            stream.Chars += segment.Text.Length;
            Append(EventTypes.StreamSegment, new { streamId = stream.StreamId, segmentId = segment.SegmentId, at = segment.At, chars = segment.Text.Length, sha256 = segment.Sha256, held = stream.Buffer.Segments.Count });
            if (Preferences.WatchedTerms.Any(t => segment.Text.Contains(t, StringComparison.OrdinalIgnoreCase))) urgent = true;
        }
        stream.WindowDirty = true;
        stream.PersistTimer ??= _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Capture.DraftPersistDebounceMs), () =>
        {
            stream.PersistTimer = null;
            if (_stream == stream && stream.WindowDirty) PersistWindow(stream);
        });
        if (urgent) JudgeNow(stream, final: false);
    }

    private void PersistWindow(StreamState stream)
    {
        try
        {
            Directory.CreateDirectory(_root.StreamDirectory);
            var payload = new { streamId = stream.StreamId, savedAt = _clock.UtcNow, windowSeconds = stream.Buffer.Window.TotalSeconds, segments = stream.Buffer.Segments.Select(s => new { s.SegmentId, s.At, s.Text }) };
            AtomicFile.WriteAllText(_root.CurrentStreamPath, JsonSerializer.Serialize(payload, RelayJson.Compact));
            stream.WindowDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notice = "The buffer window could not be saved to staging: " + ex.Message;
        }
    }

    private void ClearWindowFile()
    {
        try { if (File.Exists(_root.CurrentStreamPath)) File.Delete(_root.CurrentStreamPath); } catch (IOException) { }
    }

    private JudgeContext JudgeContextNow()
    {
        var prefs = Preferences;
        var recent = _tasks.Where(t => t.Topic is not null).Select(t => t.Topic!).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        return new JudgeContext(_services.Registry.Active.Select(p => $"{p.Name} ({p.Slug})").ToList(), prefs.WatchedTerms, recent, prefs.PromptFragment);
    }

    private void JudgeNow(StreamState stream, bool final)
    {
        if (stream.Judging) { if (final) stream.Finishing = true; return; }
        var now = _clock.UtcNow;
        stream.Buffer.Expire(now);
        var fresh = stream.Buffer.UnjudgedIds();
        if (fresh.Count == 0)
        {
            if (final) CompleteStream(stream, "stopped");
            return;
        }
        stream.Judging = true;
        stream.Passes++;
        var window = stream.Buffer.Segments.ToList();
        var request = new JudgeRequest(TaskOrigin.Observed, window, fresh, null, JudgeContextNow(), now);
        var judge = _services.Judge;
        var cts = new CancellationTokenSource();
        stream.JudgeCts = cts;
        stream.JudgeTimeout = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Judge.TimeoutMs), () => cts.Cancel());
        Task<JudgeDecision> judging;
        try { judging = judge.JudgeAsync(request, cts.Token); }
        catch (Exception ex) { judging = Task.FromException<JudgeDecision>(ex); }
        var passStarted = now;
        judging.ContinueWith(t => _scheduler.Post(() => OnJudged(stream, judge, window, fresh, t, final, passStarted)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnJudged(StreamState stream, IJudge judge, IReadOnlyList<StreamSegment> window, IReadOnlyList<string> fresh, Task<JudgeDecision> judging, bool final, DateTimeOffset passStarted)
    {
        if (_stream != stream) return;
        stream.JudgeTimeout?.Dispose();
        stream.JudgeTimeout = null;
        stream.JudgeCts?.Dispose();
        stream.JudgeCts = null;
        stream.Judging = false;
        stream.LastCheckAt = _clock.UtcNow;
        stream.LastJudge = judge.Name;
        var hashes = window.Where(s => fresh.Contains(s.SegmentId)).Select(s => s.Sha256[..16]).ToList();

        JudgeDecision decision;
        if (judging.IsCanceled) decision = JudgeDecision.Failed(judge.Name, $"timed out after {_settings.Judge.TimeoutMs} ms");
        else if (judging.IsFaulted) decision = JudgeDecision.Failed(judge.Name, judging.Exception?.GetBaseException().Message ?? "judge failed");
        else decision = judging.Result;

        if (decision.Error is not null)
        {
            stream.LastError = decision.Error;
            Append(EventTypes.ObserveFailed, new { streamId = stream.StreamId, judge = judge.Name, segments = fresh, error = decision.Error, elapsedMs = decision.ElapsedMs });
            // The model was unavailable: the heuristic judge takes this pass so nothing is silently skipped, and says so.
            if (judge is not HeuristicJudge)
            {
                var fallback = new HeuristicJudge().JudgeAsync(new JudgeRequest(TaskOrigin.Observed, window, fresh, null, JudgeContextNow(), _clock.UtcNow), CancellationToken.None).GetAwaiter().GetResult();
                decision = fallback with { Producer = HeuristicJudge.ProducerName + " (fallback)" };
            }
        }
        else stream.LastError = null;

        stream.Buffer.MarkJudged(fresh);
        var accepted = decision.Findings.Where(f => f.Confidence >= _settings.Judge.MinConfidence).ToList();
        var below = decision.Findings.Count - accepted.Count;
        if (accepted.Count == 0)
        {
            Append(EventTypes.ObserveChecked, new { streamId = stream.StreamId, judge = decision.Producer, segments = fresh, hashes, held = window.Count, promptTokens = decision.PromptTokens, completionTokens = decision.CompletionTokens, elapsedMs = decision.ElapsedMs, belowThreshold = below });
        }
        else
        {
            stream.Findings += accepted.Count;
            var found = Append(EventTypes.ObserveFound, new
            {
                streamId = stream.StreamId, judge = decision.Producer, segments = fresh, hashes, held = window.Count, promptTokens = decision.PromptTokens, completionTokens = decision.CompletionTokens, elapsedMs = decision.ElapsedMs, belowThreshold = below,
                findings = accepted.Select(f => new { kind = f.Kind.Wire(), f.Confidence, f.Summary, f.Why, segments = f.SegmentIds, f.Topic, f.ProjectHint, presentation = f.Presentation?.Wire(), f.MergeKey }),
            });
            if (found is null) return;
            var elapsed = (_clock.UtcNow - stream.StartedAt).TotalSeconds;
            foreach (var finding in accepted)
            {
                Excerpt? excerpt = null;
                var sourceEventId = found.Id;
                var segmentIds = finding.SegmentIds.Count > 0 ? finding.SegmentIds : fresh.TakeLast(1).ToList();
                if (Excerpts.Existing(segmentIds) is { } shared)
                {
                    // The same words already have an excerpt (two findings on one sentence): share it, keep nothing twice.
                    excerpt = shared;
                }
                else if (segmentIds.Any(id => stream.Buffer.Find(id) is not null))
                {
                    try
                    {
                        var guard = new RetentionGuard(Preferences.ExcerptMaxSeconds, Preferences.MaxRetainedFraction);
                        excerpt = Excerpts.Build(stream.StreamId, finding with { SegmentIds = segmentIds }, decision.Producer, stream.Buffer, guard, elapsed, _clock.UtcNow);
                        var path = Excerpts.Persist(excerpt);
                        stream.Excerpts++;
                        _services.Index.IndexExcerpt(excerpt);
                        var stored = Append(EventTypes.ExcerptStored, new { streamId = stream.StreamId, excerptId = excerpt.ExcerptId, triggerSegmentId = excerpt.TriggerSegmentId, segments = excerpt.Segments.Count, referenced = excerpt.References.Sum(r => r.SegmentIds.Count), seconds = excerpt.Seconds, chars = excerpt.Text.Length, shrunkByGuard = excerpt.ShrunkByGuard, retainedSeconds = Excerpts.RetainedSeconds, elapsedSeconds = elapsed, reason = finding.Summary, path });
                        if (stored is not null) sourceEventId = stored.Id;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        _notice = "An excerpt could not be stored: " + ex.Message;
                        excerpt = null;
                    }
                }
                stream.Tasks++;
                StartObservedTask(finding, excerpt, stream.StreamId, sourceEventId, decision.Producer);
            }
        }

        if (final || stream.Finishing) CompleteStream(stream, "stopped");
        Notify();
    }

    /// <summary>The user stopped listening: flush what is pending, judge it once more, then close the stream.</summary>
    private void FinishStream(StreamState stream)
    {
        stream.Finishing = true;
        stream.ObserveTimer?.Dispose();
        stream.ObserveTimer = null;
        stream.QuietTimer?.Dispose();
        stream.QuietTimer = null;
        AddSegments(stream, stream.Segmenter.FlushPending(_clock.UtcNow));
        JudgeNow(stream, final: true);
    }

    private void CompleteStream(StreamState stream, string reason)
    {
        if (_stream != stream) return;
        _stream = null;
        stream.ObserveTimer?.Dispose();
        stream.QuietTimer?.Dispose();
        stream.PersistTimer?.Dispose();
        stream.JudgeTimeout?.Dispose();
        stream.JudgeCts?.Cancel();
        var seconds = (_clock.UtcNow - stream.StartedAt).TotalSeconds;
        Append(EventTypes.StreamStopped, new
        {
            streamId = stream.StreamId, reason, seconds, segments = stream.Segments, chars = stream.Chars, expired = stream.Buffer.ExpiredSegments, heldAtStop = stream.Buffer.Segments.Count,
            passes = stream.Passes, findings = stream.Findings, excerpts = stream.Excerpts, tasks = stream.Tasks, retainedSeconds = Excerpts.RetainedSeconds, judge = stream.LastJudge ?? _services.Judge.Name,
        });
        stream.Buffer.Clear();
        ClearWindowFile();

        if (reason != "stopped") return;
        var draft = _draft;
        if (draft is not null && draft.CaptureId == stream.StreamId)
        {
            _draft = null;
            _draftCommittedEventId = null;
            _draftDirty = false;
        }
        if (_state == RelayState.Organizing && Apply(Trigger.OrganizeSucceeded).Accepted)
        {
            var parts = new List<string>();
            if (stream.Tasks > 0) parts.Add($"{stream.Tasks} task(s) raised");
            if (stream.Excerpts > 0) parts.Add($"{stream.Excerpts} excerpt(s) kept");
            parts.Add($"{stream.Segments} segment(s) heard, {stream.Passes} check(s)");
            ShowReceipt($"Listened {FormatSeconds(seconds)} · {string.Join(" · ", parts)}");
        }
    }

    /// <summary>A crash left the window file behind. The words expired with the buffer; only their size is recorded.</summary>
    private void DetectInterruptedStream()
    {
        var text = AtomicFile.ReadAllTextIfExists(_root.CurrentStreamPath);
        if (text is null) return;
        string? streamId = null;
        var segments = 0;
        var chars = 0;
        try
        {
            using var doc = JsonDocument.Parse(text);
            streamId = doc.RootElement.TryGetProperty("streamId", out var s) ? s.GetString() : null;
            if (doc.RootElement.TryGetProperty("segments", out var list))
                foreach (var seg in list.EnumerateArray()) { segments++; chars += seg.TryGetProperty("Text", out var t) || seg.TryGetProperty("text", out t) ? t.GetString()?.Length ?? 0 : 0; }
        }
        catch (JsonException) { }
        Append(EventTypes.StreamInterruptedFound, new { streamId, segments, chars, discarded = true });
        ClearWindowFile();
    }

    private static string FormatSeconds(double seconds)
        => seconds < 60 ? $"{seconds:0}s" : $"{(int)(seconds / 60)}m {(int)(seconds % 60):00}s";

    private ListeningStatus? ListeningView()
    {
        var s = _stream;
        if (s is null) return null;
        return new ListeningStatus(s.StreamId, s.StartedAt, s.Buffer.HeldSeconds, s.Buffer.Segments.Count, s.Segments, s.Buffer.Window.TotalSeconds, s.Passes, s.Findings, s.Excerpts, s.Tasks,
            Excerpts.RetainedSeconds, s.LastJudge ?? _services.Judge.Name, s.LastCheckAt, s.Judging, s.Finishing, s.LastError);
    }
}
