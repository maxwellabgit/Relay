using System.Text.RegularExpressions;
using Relay.Core.Ids;
using Relay.Core.Judge;

namespace Relay.Core.Stream;

/// <summary>
/// Cuts the growing capture-surface text into timestamped segments. Wispr Flow (or typing) appends
/// words to the surface; the segmenter consumes the new text at sentence ends, or after a quiet
/// period, so the buffer holds utterances rather than keystrokes. It keeps only the unconsumed tail.
/// </summary>
public sealed partial class StreamSegmenter
{
    [GeneratedRegex(@"[.!?]+[""”')\]]*\s+", RegexOptions.CultureInvariant)] private static partial Regex SentenceEnd();

    private string _seen = "";
    private string _pending = "";
    private DateTimeOffset? _pendingSince;

    public int ConsumedChars { get; private set; }
    public string Pending => _pending;

    /// <summary>
    /// Feeds the full current surface text. Returns the segments completed by this update (text that
    /// ended in a sentence terminator). Text removed from the surface (the host trimmed it) is tolerated:
    /// only the appended part counts.
    /// </summary>
    public IReadOnlyList<StreamSegment> Feed(string surfaceText, DateTimeOffset now)
    {
        string delta;
        if (surfaceText.StartsWith(_seen, StringComparison.Ordinal)) delta = surfaceText[_seen.Length..];
        else if (_seen.EndsWith(surfaceText, StringComparison.Ordinal) || surfaceText.Length < _seen.Length) delta = ""; // trimmed by the host
        else delta = surfaceText; // replaced wholesale; treat as new
        _seen = surfaceText;
        if (delta.Length == 0) return [];
        if (_pending.Length == 0) _pendingSince = now;
        _pending += delta;
        return Flush(now, force: false);
    }

    /// <summary>Called after a quiet period: whatever is pending becomes a segment even without a terminator.</summary>
    public IReadOnlyList<StreamSegment> FlushPending(DateTimeOffset now) => Flush(now, force: true);

    public bool HasPendingSince(DateTimeOffset now, TimeSpan quiet) => _pending.Trim().Length > 0 && _pendingSince is { } since && now - since >= quiet;

    private IReadOnlyList<StreamSegment> Flush(DateTimeOffset now, bool force)
    {
        var segments = new List<StreamSegment>();
        var cursor = 0;
        foreach (Match m in SentenceEnd().Matches(_pending))
        {
            var end = m.Index + m.Length;
            var text = _pending[cursor..end].Trim();
            if (text.Length > 0) segments.Add(new StreamSegment(Ulid.NewUlid(now), now, text));
            cursor = end;
        }
        if (force)
        {
            var rest = _pending[cursor..].Trim();
            if (rest.Length > 0) segments.Add(new StreamSegment(Ulid.NewUlid(now), now, rest));
            cursor = _pending.Length;
        }
        ConsumedChars += cursor;
        _pending = _pending[cursor..];
        _pendingSince = _pending.Length == 0 ? null : now;
        return segments;
    }

    /// <summary>The host trimmed the surface; forget the prefix so the next feed lines up.</summary>
    public void SurfaceTrimmed(string newSurfaceText) => _seen = newSurfaceText;

    public void Reset()
    {
        _seen = "";
        _pending = "";
        _pendingSince = null;
        ConsumedChars = 0;
    }
}

/// <summary>
/// What is held while listening. With a window, segments older than it are dropped whenever the buffer is
/// touched, so it holds at most the last window of talk. With <see cref="TimeSpan.Zero"/> it holds the whole
/// conversation until the stream stops and the buffer is cleared: nothing is removed before it is clear what
/// can be removed easily. Either way the words live here and in excerpts only, never in the ledger.
/// </summary>
public sealed class ConversationBuffer
{
    private readonly List<StreamSegment> _segments = new();
    private readonly HashSet<string> _judged = new(StringComparer.Ordinal);

    public ConversationBuffer(TimeSpan window) => Window = window < TimeSpan.Zero ? TimeSpan.Zero : window;

    /// <summary>The rolling window, or zero when the whole conversation is held.</summary>
    public TimeSpan Window { get; }
    public bool HoldsWholeConversation => Window == TimeSpan.Zero;
    public int TotalSegments { get; private set; }
    public int TotalChars { get; private set; }
    public int ExpiredSegments { get; private set; }
    public DateTimeOffset? FirstAt { get; private set; }
    public DateTimeOffset? LastAt { get; private set; }

    public IReadOnlyList<StreamSegment> Segments => _segments;

    public void Append(StreamSegment segment, DateTimeOffset now)
    {
        _segments.Add(segment);
        TotalSegments++;
        TotalChars += segment.Text.Length;
        FirstAt ??= segment.At;
        LastAt = segment.At;
        Expire(now);
    }

    /// <summary>Drops everything older than the window (nothing when the whole conversation is held). Returns how many segments were dropped by this call.</summary>
    public int Expire(DateTimeOffset now)
    {
        if (HoldsWholeConversation) return 0;
        var cutoff = now - Window;
        var dropped = 0;
        while (_segments.Count > 0 && _segments[0].At < cutoff)
        {
            _judged.Remove(_segments[0].SegmentId);
            _segments.RemoveAt(0);
            dropped++;
        }
        ExpiredSegments += dropped;
        return dropped;
    }

    public IReadOnlyList<string> UnjudgedIds() => _segments.Where(s => !_judged.Contains(s.SegmentId)).Select(s => s.SegmentId).ToList();

    /// <summary>How much unjudged talk is waiting: its characters, and the age of the oldest unjudged segment.</summary>
    public (int Chars, TimeSpan Age) Unjudged(DateTimeOffset now)
    {
        var chars = 0;
        DateTimeOffset? oldest = null;
        foreach (var s in _segments)
        {
            if (_judged.Contains(s.SegmentId)) continue;
            chars += s.Text.Length;
            if (oldest is null || s.At < oldest) oldest = s.At;
        }
        return (chars, oldest is null ? TimeSpan.Zero : now - oldest.Value);
    }

    public void MarkJudged(IEnumerable<string> ids)
    {
        foreach (var id in ids) _judged.Add(id);
    }

    public StreamSegment? Find(string segmentId) => _segments.FirstOrDefault(s => s.SegmentId == segmentId);

    /// <summary>Seconds of talk currently held, measured from the oldest to the newest segment.</summary>
    public double HeldSeconds => _segments.Count == 0 ? 0 : (_segments[^1].At - _segments[0].At).TotalSeconds;

    public void Clear()
    {
        _segments.Clear();
        _judged.Clear();
    }
}
