using Relay.Core.Cases;
using Relay.Core.Time;

namespace Relay.Core.Listening;

/// <summary>
/// Forms durable listening windows from captured segments. Capture stays in <see cref="StreamIntake"/>;
/// this controller owns coalesce/split/coverage and never drops uncovered backlog to a recent-N slice.
/// </summary>
public sealed class ListeningController
{
    private readonly ListeningWindowStore _store;
    private readonly IClock _clock;

    public ListeningController(ListeningWindowStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public ListeningWindowStore Store => _store;

    /// <summary>
    /// Sync durable windows with the full segment backlog. Prefers finalized utterances; coalesces gaps ≤2s;
    /// caps primary span ≤30s and ≤8000 chars; preceding overlap ≤60s is context-only.
    /// Older uncovered segments are windowed before any drop consideration.
    /// Never collapses backlog to the most recent 32 segments.
    /// </summary>
    public IReadOnlyList<ListeningWindow> FormWindows(
        string sessionId,
        string? caseId,
        IReadOnlyList<SequencedSegment> segments,
        IReadOnlySet<string>? alreadyCoveredPrimary = null,
        IReadOnlyDictionary<string, string>? decisionVersions = null)
    {
        // Prefer finalized utterances when interims exist with the same sequence neighborhood.
        var preferred = PreferFinalized(segments);
        alreadyCoveredPrimary ??= CoveredPrimarySegmentIds(sessionId);

        var uncovered = preferred
            .Where(s => !alreadyCoveredPrimary.Contains(s.SegmentId))
            .OrderBy(s => s.Sequence)
            .ToList();

        // Explicitly retain full backlog — do not TakeLast(32).
        _ = ListeningWindowPolicy.ForbiddenRecentOnlyBacklogLimit;

        var touched = new List<ListeningWindow>();
        if (uncovered.Count == 0) return touched;

        // Try to extend the newest still-pending window before opening new ones.
        var pending = _store.ListPending(sessionId)
            .Where(w => w.Status == ListeningWindowStatus.Pending)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefault();

        var i = 0;
        if (pending is not null)
        {
            while (i < uncovered.Count && TryExtend(pending, uncovered[i], preferred))
            {
                touched.Add(pending);
                i++;
            }
            if (touched.Count > 0)
                _store.Save(pending);
        }

        while (i < uncovered.Count)
        {
            var batch = new List<SequencedSegment> { uncovered[i] };
            var startTs = uncovered[i].Ts;
            var chars = uncovered[i].Text.Length;
            var j = i + 1;
            while (j < uncovered.Count)
            {
                var next = uncovered[j];
                var gap = next.Ts - batch[^1].Ts;
                var span = next.Ts - startTs;
                var nextChars = chars + 1 + next.Text.Length;
                if (gap > ListeningWindowPolicy.CoalesceGap) break;
                if (span > ListeningWindowPolicy.MaxPrimarySpan) break;
                if (nextChars > ListeningWindowPolicy.MaxWindowChars) break;
                batch.Add(next);
                chars = nextChars;
                j++;
            }

            if (batch.Count == 1 && batch[0].Text.Length > ListeningWindowPolicy.MaxWindowChars)
            {
                foreach (var piece in SplitAtSentences(batch[0], ListeningWindowPolicy.MaxWindowChars))
                    touched.Add(CreateWindow(sessionId, caseId, piece, preferred, decisionVersions));
                i = j;
                continue;
            }

            touched.Add(CreateWindow(sessionId, caseId, batch, preferred, decisionVersions));
            i = j;
        }

        return touched;
    }

    /// <summary>Mark explicit primary segment coverage. Completes the window only when all primary ids are covered. No first-pending fallback.</summary>
    public bool MarkCovered(string windowId, IReadOnlyList<string> primarySegmentIds, IEnumerable<string>? findingIds = null)
    {
        var window = _store.TryLoad(windowId);
        if (window is null) return false;
        if (primarySegmentIds.Count == 0) return false;

        foreach (var id in primarySegmentIds)
        {
            if (!window.Primary.ContainsSegment(id))
                return false;
        }

        if (findingIds is not null)
        {
            foreach (var f in findingIds)
            {
                if (!window.FindingIds.Contains(f, StringComparer.Ordinal))
                    window.FindingIds.Add(f);
            }
        }

        // Persist per-segment coverage notes; complete only when every primary id is noted.
        foreach (var id in primarySegmentIds)
        {
            var marker = "covered:" + id;
            if (!window.Notes.Contains(marker, StringComparer.Ordinal))
                window.Notes.Add(marker);
        }

        var allCovered = window.Primary.SegmentIds.All(id =>
            window.Notes.Contains("covered:" + id, StringComparer.Ordinal));
        if (allCovered)
            window.Status = ListeningWindowStatus.Completed;
        else if (window.Status == ListeningWindowStatus.Pending)
            window.Status = ListeningWindowStatus.Processing;

        _store.Save(window);
        return true;
    }

    /// <summary>Refuse to mark coverage without explicit segment ids.</summary>
    public bool TryMarkCoveredWithoutExplicitIds(string windowId) => false;

    /// <summary>Segments covered as primary across all non-failed windows (pending counts as reserved).</summary>
    public HashSet<string> CoveredPrimarySegmentIds(string sessionId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in _store.ListBySession(sessionId))
        {
            if (w.Status == ListeningWindowStatus.Failed) continue;
            foreach (var id in w.Primary.SegmentIds)
                set.Add(id);
        }
        return set;
    }

    public bool HasCoverageGaps(string sessionId, IEnumerable<string> allSegmentIds)
    {
        var covered = CoveredPrimarySegmentIds(sessionId);
        return allSegmentIds.Any(id => !covered.Contains(id));
    }

    /// <summary>During outages keep pending windows durable; never collapse to recent-only backlog.</summary>
    public IReadOnlyList<ListeningWindow> PendingThroughOutage(string sessionId)
        => _store.ListPending(sessionId);

    private bool TryExtend(ListeningWindow pending, SequencedSegment next, IReadOnlyList<SequencedSegment> all)
    {
        if (pending.Primary.EndTs is null || pending.Primary.StartTs is null) return false;
        var gap = next.Ts - pending.Primary.EndTs.Value;
        var span = next.Ts - pending.Primary.StartTs.Value;
        var nextChars = pending.Primary.CharCount + 1 + next.Text.Length;
        if (gap > ListeningWindowPolicy.CoalesceGap) return false;
        if (span > ListeningWindowPolicy.MaxPrimarySpan) return false;
        if (nextChars > ListeningWindowPolicy.MaxWindowChars) return false;

        pending.Primary.SegmentIds.Add(next.SegmentId);
        pending.Primary.EndSequence = next.Sequence;
        pending.Primary.EndTs = next.Ts;
        pending.Primary.CharCount = nextChars;
        pending.Text = string.IsNullOrEmpty(pending.Text) ? next.Text : pending.Text + "\n" + next.Text;

        // Refresh context from preceding overlap (context only).
        var contextCutoff = pending.Primary.StartTs.Value - ListeningWindowPolicy.MaxPrecedingOverlap;
        var contextSegs = all
            .Where(s => s.Ts < pending.Primary.StartTs && s.Ts >= contextCutoff
                        && !pending.Primary.ContainsSegment(s.SegmentId))
            .OrderBy(s => s.Sequence)
            .ToList();
        pending.Context = new ListeningCoverage
        {
            Role = CoverageRoles.Context,
            SegmentIds = contextSegs.Select(s => s.SegmentId).ToList(),
            StartSequence = contextSegs.Count > 0 ? contextSegs[0].Sequence : 0,
            EndSequence = contextSegs.Count > 0 ? contextSegs[^1].Sequence : 0,
            StartTs = contextSegs.Count > 0 ? contextSegs[0].Ts : null,
            EndTs = contextSegs.Count > 0 ? contextSegs[^1].Ts : null,
            CharCount = contextSegs.Sum(s => s.Text.Length),
        };
        return true;
    }

    private ListeningWindow CreateWindow(
        string sessionId,
        string? caseId,
        IReadOnlyList<SequencedSegment> primaryBatch,
        IReadOnlyList<SequencedSegment> all,
        IReadOnlyDictionary<string, string>? decisionVersions)
    {
        var primaryStart = primaryBatch[0].Ts;
        var contextCutoff = primaryStart - ListeningWindowPolicy.MaxPrecedingOverlap;
        var contextSegs = all
            .Where(s => s.Ts < primaryStart && s.Ts >= contextCutoff
                        && primaryBatch.All(p => p.SegmentId != s.SegmentId))
            .OrderBy(s => s.Sequence)
            .ToList();

        var primaryText = string.Join("\n", primaryBatch.Select(s => s.Text));
        var primary = new ListeningCoverage
        {
            Role = CoverageRoles.Primary,
            SegmentIds = primaryBatch.Select(s => s.SegmentId).ToList(),
            StartSequence = primaryBatch[0].Sequence,
            EndSequence = primaryBatch[^1].Sequence,
            StartTs = primaryBatch[0].Ts,
            EndTs = primaryBatch[^1].Ts,
            CharCount = primaryText.Length,
        };
        var context = new ListeningCoverage
        {
            Role = CoverageRoles.Context,
            SegmentIds = contextSegs.Select(s => s.SegmentId).ToList(),
            StartSequence = contextSegs.Count > 0 ? contextSegs[0].Sequence : 0,
            EndSequence = contextSegs.Count > 0 ? contextSegs[^1].Sequence : 0,
            StartTs = contextSegs.Count > 0 ? contextSegs[0].Ts : null,
            EndTs = contextSegs.Count > 0 ? contextSegs[^1].Ts : null,
            CharCount = contextSegs.Sum(s => s.Text.Length),
        };

        return _store.Create(sessionId, caseId, primary, context, primaryText, decisionVersions);
    }

    private static IReadOnlyList<SequencedSegment> PreferFinalized(IReadOnlyList<SequencedSegment> segments)
    {
        // Keep chronological order; drop non-finalized when a finalized segment shares the same text+speaker nearby.
        var result = new List<SequencedSegment>();
        foreach (var s in segments.OrderBy(x => x.Sequence))
        {
            if (!s.Finalized
                && segments.Any(o => o.Finalized
                    && o.Speaker == s.Speaker
                    && o.Text == s.Text
                    && Math.Abs((o.Ts - s.Ts).TotalSeconds) <= 2))
                continue;
            result.Add(s);
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<SequencedSegment>> SplitAtSentences(SequencedSegment segment, int maxChars)
    {
        var pieces = new List<IReadOnlyList<SequencedSegment>>();
        var text = segment.Text;
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxChars);
            if (end < text.Length)
            {
                var slice = text[start..end];
                var lastStop = Math.Max(slice.LastIndexOf('.'), Math.Max(slice.LastIndexOf('!'), slice.LastIndexOf('?')));
                if (lastStop > 0) end = start + lastStop + 1;
            }
            var pieceText = text[start..end];
            pieces.Add([segment with { Text = pieceText }]);
            start = end;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
        return pieces;
    }
}

/// <summary>Captured segment with durable sequence for coverage tracking.</summary>
public sealed record SequencedSegment(
    string SegmentId,
    int Sequence,
    DateTimeOffset Ts,
    string Text,
    string? Speaker = null,
    bool Finalized = true);
