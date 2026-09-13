using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Stream;

namespace Relay.Core.Session;

/// <summary>
/// Listening. The note chord opens a stream instead of a recording: the surface text is cut into
/// timestamped segments, held in a rolling buffer that expires continuously, and read by the mind
/// every few seconds. What the mind raises persists an excerpt and becomes a task of its own;
/// everything else is gone within the window. The ledger sees ids, hashes, sizes, tokens and timings —
/// never the words. The full surface text stays in memory for the user to see and is dropped when the
/// stream stops. The pass itself is in <see cref="SessionCoordinator"/>'s Observe part.
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
        public IDisposable? PassTimeout { get; set; }
        public CancellationTokenSource? PassCts { get; set; }
        /// <summary>The pass in flight ran out of time. The loop only knows it was cancelled; this is why.</summary>
        public bool PassTimedOut { get; set; }
        /// <summary>A pass is in flight. One at a time per conversation; new talk waits for the next.</summary>
        public bool Reading { get; set; }
        public bool Finishing { get; set; }
        public bool WindowDirty { get; set; }
        public int Passes { get; set; }
        public int Excerpts { get; set; }
        public int Tasks { get; set; }
        public int Segments { get; set; }
        public int Chars { get; set; }
        public DateTimeOffset? LastCheckAt { get; set; }
        public string? LastError { get; set; }
        /// <summary>The conversation's own loop: the mind while Relay is listening, one pass per stretch of talk.</summary>
        public required Mind.ObservingLoop Loop { get; init; }
        /// <summary>segmentId → the label (<c>#1</c>, <c>#2</c>, …) the mind has been shown for it, kept for the life of the stream.</summary>
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        public int Labelled { get; set; }
        /// <summary>The mind's last line about what it heard, for the listening view. Never in the ledger unguarded.</summary>
        public string? LastSaid { get; set; }
    }

    private StreamState? _stream;

    /// <summary>Whether the note chord listens, or dictates one silent note. Listening needs a mind; without one there is nothing to read with.</summary>
    public bool ListeningEnabled => _settings.Listening.Enabled && _services.Mind is not null;

    private bool IsStreaming(Captures.CaptureDraft? draft) => _stream is not null && draft is not null && _stream.StreamId == draft.CaptureId;

    private void BeginStream(Captures.CaptureDraft draft)
    {
        var now = _clock.UtcNow;
        // 0 holds the whole conversation until the stream stops; otherwise the shorter of the setting and the user's retention preference, never under 15 s.
        var window = _settings.Stream.BufferSeconds == StreamSettings.WholeConversation
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(15, Math.Min(_settings.Stream.BufferSeconds, Preferences.Buffer.TotalSeconds > 0 ? Preferences.Buffer.TotalSeconds : _settings.Stream.BufferSeconds)));
        var buffer = new ConversationBuffer(window);
        var segmenter = new StreamSegmenter();
        _stream = new StreamState
        {
            StreamId = draft.CaptureId,
            StartedAt = now,
            Segmenter = segmenter,
            Buffer = buffer,
            Loop = NewObservingLoop(draft.CaptureId),
        };
        Excerpts.BeginStream();
        Append(EventTypes.StreamStarted, new
        {
            streamId = draft.CaptureId, bufferSeconds = window.TotalSeconds, wholeConversation = window == TimeSpan.Zero, observeIntervalMs = _settings.Stream.ObserveIntervalMs,
            minIngestChars = _settings.Stream.MinIngestChars, minIngestSeconds = _settings.Stream.MinIngestSeconds,
            mind = _stream.Loop.MindName, previousForegroundProcess = draft.PreviousForegroundProcess,
        });
        ScheduleObserve();
    }

    /// <summary>
    /// The slower ingest: a pass runs only once enough new talk has gathered (<c>minIngestChars</c>) or the oldest of it
    /// has waited long enough (<c>minIngestSeconds</c>), so the mind reads a stretch of conversation rather than each fragment.
    /// </summary>
    private bool EnoughToIngest(StreamState stream, DateTimeOffset now)
    {
        var (chars, age) = stream.Buffer.Unread(now);
        if (chars == 0) return false;
        var s = _settings.Stream;
        return chars >= s.MinIngestChars || age.TotalSeconds >= s.MinIngestSeconds;
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
            ReadNow(stream, final: false);
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
        if (urgent) ReadNow(stream, final: false, urgent: true);
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

    /// <summary>
    /// One pass of the mind over what has arrived, if anything has and enough of it. A pass already in flight
    /// holds the next: the conversation is read one stretch at a time, and nothing heard while a pass runs is lost.
    /// </summary>
    private void ReadNow(StreamState stream, bool final, bool urgent = false)
    {
        if (stream.Reading) { if (final) stream.Finishing = true; return; }
        var now = _clock.UtcNow;
        stream.Buffer.Expire(now);
        if (stream.Buffer.UnreadIds().Count == 0)
        {
            if (final) CompleteStream(stream, "stopped");
            return;
        }
        // Not yet: let the conversation run on. The final pass and a watched term never wait.
        if (!final && !urgent && !EnoughToIngest(stream, now)) return;
        ObservePass(stream, final);
    }

    /// <summary>The user stopped listening: flush what is pending, read it once more, then close the stream.</summary>
    private void FinishStream(StreamState stream)
    {
        stream.Finishing = true;
        stream.ObserveTimer?.Dispose();
        stream.ObserveTimer = null;
        stream.QuietTimer?.Dispose();
        stream.QuietTimer = null;
        AddSegments(stream, stream.Segmenter.FlushPending(_clock.UtcNow));
        ReadNow(stream, final: true);
    }

    private void CompleteStream(StreamState stream, string reason)
    {
        if (_stream != stream) return;
        _stream = null;
        stream.ObserveTimer?.Dispose();
        stream.QuietTimer?.Dispose();
        stream.PersistTimer?.Dispose();
        stream.PassTimeout?.Dispose();
        stream.PassCts?.Cancel();
        var seconds = (_clock.UtcNow - stream.StartedAt).TotalSeconds;
        Append(EventTypes.StreamStopped, new
        {
            streamId = stream.StreamId, reason, seconds, segments = stream.Segments, chars = stream.Chars, expired = stream.Buffer.ExpiredSegments, heldAtStop = stream.Buffer.Segments.Count,
            passes = stream.Passes, excerpts = stream.Excerpts, tasks = stream.Tasks, retainedSeconds = Excerpts.RetainedSeconds, mind = stream.Loop.MindName,
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
        if (_capture == CapturePhase.Organizing)
        {
            _capture = CapturePhase.None;
            var parts = new List<string>();
            if (stream.Tasks > 0) parts.Add($"{stream.Tasks} task(s) raised");
            if (stream.Excerpts > 0) parts.Add($"{stream.Excerpts} excerpt(s) kept");
            parts.Add($"{stream.Segments} segment(s) heard, {stream.Passes} pass(es)");
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
        return new ListeningStatus(s.StreamId, s.StartedAt, s.Buffer.HeldSeconds, s.Buffer.Segments.Count, s.Segments, s.Buffer.Window.TotalSeconds, s.Passes, s.Loop.Raises, s.Excerpts, s.Tasks,
            Excerpts.RetainedSeconds, s.Loop.MindName, s.LastCheckAt, s.Reading, s.Finishing, s.LastError);
    }
}
